using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Contracts;
using Z.Vtuber.RecordingTranscript.Api.Controllers;
using Z.Vtuber.RecordingTranscript.Api.Sse;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;

namespace Z.Vtuber.Tests;

/// <summary>
/// 入队创建提交信息日志断言（Plan 1.6 / 2.4③④）：
/// 手动 Enqueue 建任务成功 → Info 含 taskId/recordingId/roomId/file/modelId/mode/file source/manual；
/// 409 幂等命中 → Debug；自动 EnsureTaskAsync 建任务成功 → 同字段 Info（mode=live|file、source=auto）；
/// 所有日志不含模型 apiKey 明文。
/// </summary>
[Collection("redis")]
public class TranscriptSubmitLogTests(MongoDbFixture mongo, RedisFixture redis)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    /// <summary>明显的假 apiKey：任何日志条目都不得出现该明文。</summary>
    private const string FakeApiKey = "PLAINTEXT-API-KEY-DO-NOT-LOG-000112233445566778899";

    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);
    private TranscriptTaskRepository Tasks => new(mongo.Database);
    private TranscriptModelRepository Models => new(mongo.Database);
    private TranscriptAutoEnqueueRepository AutoEnqueue => new(mongo.Database);

    private static readonly IOptions<TranscriptApiOptions> DefaultOptions =
        Options.Create(new TranscriptApiOptions());

    private TranscriptTasksController CreateTasksController(
        ListLogger<TranscriptTasksController> logger) =>
        WithContext(new TranscriptTasksController(
            DefaultOptions,
            Recordings,
            Tasks,
            new TranscriptSegmentRepository(mongo.Database),
            Models,
            new TranscriptSegmentStream(
                new TranscriptSegmentRepository(mongo.Database),
                Tasks,
                redis.Connection,
                NullLogger<TranscriptSegmentStream>.Instance),
            logger));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private async Task<RecordingRecord> SeedEndedRecordingAsync(
        long roomId, long mid, params string[] files)
    {
        var record = new RecordingRecord
        {
            RoomId = roomId,
            Mid = mid,
            Title = "场次",
            Files = [.. files],
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        await Recordings.InsertAsync(record);
        return record;
    }

    private async Task<ObjectId> InsertModelAsync(string name = "qwen-fun-asr") =>
        await Models.InsertAsync(new TranscriptModelRecord
        {
            Name = name,
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = FakeApiKey,
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = 2,
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<ObjectId> SeedRecordingAsync(long roomId, params string[] files)
    {
        var id = await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "场次",
            Files = [.. files],
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow,
        });
        return id;
    }

    // ---- 手动入队（2.4③）----

    [Fact]
    public async Task ManualEnqueue_Created_LogsSubmitInfoWithoutApiKey()
    {
        var recording = await SeedEndedRecordingAsync(700130, 555, "[AV1] a.flv", "[RAW] a.flv");
        var modelId = await InsertModelAsync();
        var logger = new ListLogger<TranscriptTasksController>();

        var json = Ok(await CreateTasksController(logger)
            .Enqueue(new EnqueueRequest(recording.Id.ToString(), modelId.ToString())));

        var taskId = json.GetProperty("taskId").GetString();
        var entry = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("transcript task enqueued:"));
        Assert.Contains($"taskId={taskId}", entry.Message);
        Assert.Contains($"recordingId={recording.Id}", entry.Message);
        Assert.Contains("roomId=700130", entry.Message);
        Assert.Contains("file=[RAW] a.flv", entry.Message);
        Assert.Contains($"modelId={modelId}", entry.Message);
        Assert.Contains("mode=file", entry.Message);
        Assert.Contains("source=manual", entry.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }

    [Fact]
    public async Task ManualEnqueue_Duplicate_LogsDebugIdempotentHit()
    {
        var recording = await SeedEndedRecordingAsync(700131, 555, "[RAW] a.flv");
        var modelId = await InsertModelAsync();
        var logger = new ListLogger<TranscriptTasksController>();
        var controller = CreateTasksController(logger);
        var request = new EnqueueRequest(recording.Id.ToString(), modelId.ToString());

        var first = Ok(await controller.Enqueue(request));
        var second = await controller.Enqueue(request);

        Assert.IsType<ConflictObjectResult>(second);
        var firstTaskId = first.GetProperty("taskId").GetString();
        var debug = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("transcript task enqueued:"));
        Assert.Contains($"taskId={firstTaskId}", debug.Message);
        Assert.Contains($"recordingId={recording.Id}", debug.Message);
        Assert.Contains("mode=", debug.Message);
        Assert.Contains("source=manual", debug.Message);
        // 建任务成功的 Info 只应有一条（首次），409 不重复记 Info
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }

    // ---- 自动建任务（2.4④）----

    [Fact]
    public async Task AutoEnsureTask_Created_LogsSameFieldsWithoutApiKey()
    {
        var logger = new ListLogger<TranscriptEnqueueService>();
        var enqueue = new TranscriptEnqueueService(
            Recordings, Tasks, Models, AutoEnqueue, logger);
        var modelId = await InsertModelAsync();

        // live：开播事件建 mode=live/source=auto 任务
        var liveRecordingId = await SeedRecordingAsync(700132, "[RAW]live.flv", "[AV1]live.flv");
        await AutoEnqueue.UpsertAsync(700132, 700133, enabled: true, modelId);
        var startedTaskId = await enqueue.EnqueueForStartedAsync(new RecordingStartedEvent(
            liveRecordingId.ToString(), 700132, 700133, "场次", "http://rec/stream/audio", DateTime.UtcNow));

        // file：结束事件补建 mode=file/source=auto 任务
        var fileRecordingId = await SeedRecordingAsync(700134, "[RAW]file.flv");
        await Recordings.SetEndedAtAsync(fileRecordingId, DateTime.UtcNow.AddMinutes(5));
        await AutoEnqueue.UpsertAsync(700134, 700135, enabled: true, modelId);
        var finishedTaskId = await enqueue.EnqueueForFinishedAsync(new RecordingFinishedEvent(
            fileRecordingId.ToString(), 700134, 700135, "场次", ["[RAW]file.flv"], [],
            DateTime.UtcNow.AddMinutes(6)));

        var infos = logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("transcript task enqueued:"))
            .ToList();
        Assert.Equal(2, infos.Count);

        var live = Assert.Single(infos, e => e.Message.Contains("mode=live"));
        Assert.Contains("source=auto", live.Message);
        Assert.Contains($"taskId={startedTaskId}", live.Message);
        Assert.Contains($"recordingId={liveRecordingId}", live.Message);
        Assert.Contains("roomId=700132", live.Message);
        Assert.Contains("file=[RAW]live.flv", live.Message);
        Assert.Contains($"modelId={modelId}", live.Message);

        var file = Assert.Single(infos, e => e.Message.Contains("mode=file"));
        Assert.Contains("source=auto", file.Message);
        Assert.Contains($"taskId={finishedTaskId}", file.Message);
        Assert.Contains($"recordingId={fileRecordingId}", file.Message);
        Assert.Contains("roomId=700134", file.Message);
        Assert.Contains("file=[RAW]file.flv", file.Message);
        Assert.Contains($"modelId={modelId}", file.Message);

        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }
}
