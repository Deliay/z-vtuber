using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Context;
using Z.Vtuber.RecordingTranscript.Consumer.Services;
using Z.Vtuber.RecordingTranscript.Consumer.Tasks;

namespace Z.Vtuber.Tests;

/// <summary>
/// 识别上下文日志断言（Plan 1.7 / 2.4①②）：
/// run-task 提交日志打印具体上下文全文（contextLines/contextChars/context=、空上下文、2KB 截断标识）、
/// 任务启动组装完成 Info 打印 taskId/lines/chars/context=（同截断口径，覆盖 ASR 连接失败路径）；
/// 所有日志不含模型 apiKey 明文。
/// </summary>
public class AliFunAsrContextLogTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    /// <summary>明显的假 apiKey：任何日志条目都不得出现该明文。</summary>
    private const string FakeApiKey = "PLAINTEXT-API-KEY-DO-NOT-LOG-000112233445566778899";

    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptTaskRepository Tasks => new(mongo.Database);

    private TranscriptModelRepository Models => new(mongo.Database);

    private RecordingRepository Recordings => new(mongo.Database);

    private static AliFunAsrService CreateService(
        string url, IReadOnlyList<string> context, ILogger<AliFunAsrService>? logger = null) =>
        new(new Uri(url), FakeApiKey, 0, "qwen-fun-asr-streaming", context,
            TimeSpan.FromSeconds(5),
            logger ?? NullLogger<AliFunAsrService>.Instance);

    private static async Task CollectAsync(
        IAsyncEnumerable<StreamingAsrSegment> source)
    {
        await foreach (var _ in source)
        {
            // 只关心日志，片段本身无关
        }
    }

    // ---- run-task 提交日志（2.4①）----

    [Fact]
    public async Task RunTask_ThreeContextLines_LogsFullContextWithCounts()
    {
        await using var server = new FakeFunAsrServer();
        var logger = new ListLogger<AliFunAsrService>();
        string[] context = ["主播名:某主播", "舰长:舰长A", "SC:打call内容"];
        var service = CreateService(server.Url, context, logger);

        await CollectAsync(service.TranscribeAsync(new MemoryStream(new byte[10])));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("run-task sent"));
        var joined = string.Join("\n", context);
        Assert.Contains("contextLines=3", entry.Message);
        Assert.Contains($"contextChars={joined.Length}", entry.Message);
        Assert.Contains($"context={joined}", entry.Message);
        foreach (var line in context)
        {
            Assert.Contains(line, entry.Message);
        }

        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }

    [Fact]
    public async Task RunTask_EmptyContext_LogsEmptyContextMarker()
    {
        await using var server = new FakeFunAsrServer();
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(server.Url, [], logger);

        await CollectAsync(service.TranscribeAsync(new MemoryStream(new byte[10])));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("run-task sent"));
        Assert.Contains("contextLines=0", entry.Message);
        Assert.Contains("contextChars=0", entry.Message);
        Assert.Contains("空上下文", entry.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }

    [Fact]
    public async Task RunTask_OverlongContext_TruncatesWithMarker()
    {
        await using var server = new FakeFunAsrServer();
        var logger = new ListLogger<AliFunAsrService>();
        var padding = new string('a', 4000);
        const string tail = "TAIL-BEYOND-2KB-MARKER";
        var full = padding + tail;
        var service = CreateService(server.Url, [full], logger);

        await CollectAsync(service.TranscribeAsync(new MemoryStream(new byte[10])));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("run-task sent"));
        // 2KB 截断口径：带标记、不含完整原文（2KB 之后的尾部不得出现）
        Assert.Contains("...(truncated)", entry.Message);
        Assert.DoesNotContain(full, entry.Message);
        Assert.DoesNotContain(tail, entry.Message);
        // contextChars 记录截断前的真实上下文长度
        Assert.Contains($"contextChars={full.Length}", entry.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }

    // ---- 任务启动组装完成 Info（2.4②）----

    [Fact]
    public async Task TaskStart_ContextAssembled_LogsLinesCharsAndFullContext()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(130);
        var taskId = await InsertTaskAsync(recordingId, modelId);

        var logger = new ListLogger<TranscriptTaskRunner>();
        var contextBuilder = new FakeTranscriptContextBuilder { Result = ["主播名", "舰长A"] };
        var runner = CreateRunner(logger, contextBuilder);

        await runner.RunAsync((await Tasks.GetByIdAsync(taskId))!);

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("transcript context assembled"));
        Assert.Equal(LogLevel.Information, entry.Level);
        var joined = "主播名\n舰长A";
        Assert.Contains($"taskId={taskId}", entry.Message);
        Assert.Contains("lines=2", entry.Message);
        Assert.Contains($"chars={joined.Length}", entry.Message);
        Assert.Contains($"context={joined}", entry.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(FakeApiKey, e.Message));
    }

    private TranscriptTaskRunner CreateRunner(
        ILogger<TranscriptTaskRunner> logger, ITranscriptContextBuilder contextBuilder) =>
        new(Tasks, Models, Recordings,
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(),
            new FakeStreamingAsrServiceFactory
            {
                Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "a")]),
            },
            contextBuilder,
            new MongoTranscriptSegmentPersister(mongo.Database),
            new FakeTranscriptSegmentPublisher(),
            logger);

    private async Task<ObjectId> InsertModelAsync() =>
        await Models.InsertAsync(new TranscriptModelRecord
        {
            Name = "model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = FakeApiKey,
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = 4,
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<ObjectId> InsertRecordingAsync(long roomId) =>
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = ["[RAW]a.flv"],
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        });

    private async Task<ObjectId> InsertTaskAsync(ObjectId recordingId, ObjectId modelId) =>
        await Tasks.InsertAsync(new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = 130,
            File = "[RAW]a.flv",
            ModelId = modelId,
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Auto,
            Position = 0,
            Status = TranscriptTaskStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
}
