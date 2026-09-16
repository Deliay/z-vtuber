using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Contracts;
using Z.Vtuber.RecordingTranscript.Api.Controllers;
using Z.Vtuber.RecordingTranscript.Api.Sse;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class TranscriptApiTests(MongoDbFixture mongo, RedisFixture redis)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);
    private TranscriptTaskRepository Tasks => new(mongo.Database);
    private TranscriptSegmentRepository Segments => new(mongo.Database);
    private TranscriptModelRepository Models => new(mongo.Database);
    private TranscriptAutoEnqueueRepository AutoEnqueue => new(mongo.Database);
    private AccountBasicInfoRepository AccountInfo => new(mongo.Database);

    private static readonly IOptions<TranscriptApiOptions> DefaultOptions =
        Options.Create(new TranscriptApiOptions());

    private RecordingsArchiveController CreateArchiveController(
        IOptions<TranscriptApiOptions>? options = null) =>
        WithContext(new RecordingsArchiveController(
            options ?? DefaultOptions, Recordings, Tasks, Models, AutoEnqueue));

    private TranscriptTasksController CreateTasksController(
        IOptions<TranscriptApiOptions>? options = null) =>
        WithContext(new TranscriptTasksController(
            options ?? DefaultOptions,
            Recordings,
            Tasks,
            Segments,
            Models,
            new TranscriptSegmentStream(Segments, Tasks, redis.Connection, NullLogger<TranscriptSegmentStream>.Instance)));

    private TranscriptSettingsController CreateSettingsController() =>
        WithContext(new TranscriptSettingsController(AutoEnqueue, AccountInfo));

    private TranscriptModelsController CreateModelsController() =>
        WithContext(new TranscriptModelsController(Models));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private async Task<RecordingRecord> SeedRecordingAsync(
        long roomId, long mid, string title, params string[] files)
    {
        var record = new RecordingRecord
        {
            RoomId = roomId,
            Mid = mid,
            Title = title,
            Files = [.. files],
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        await Recordings.InsertAsync(record);
        return record;
    }

    private async Task<RecordingRecord> SeedEndedRecordingAsync(
        long roomId, long mid, string title, params string[] files)
    {
        var record = await SeedRecordingAsync(roomId, mid, title, files);
        record.EndedAt = DateTime.UtcNow;
        await Recordings.SetEndedAtAsync(record.Id, record.EndedAt.Value);
        return record;
    }

    private async Task<TranscriptModelRecord> SeedModelAsync(
        string name = "qwen-fun-asr", bool enabled = true, bool isDefault = false)
    {
        var model = new TranscriptModelRecord
        {
            Name = name,
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "super-secret",
            Enabled = enabled,
            IsDefault = isDefault,
            MaxConcurrency = 1,
            CreatedAt = DateTime.UtcNow,
        };
        await Models.InsertAsync(model);
        return model;
    }

    private async Task<TranscriptTaskRecord> SeedTaskAsync(
        ObjectId recordingId, ObjectId modelId, long roomId,
        string status = TranscriptTaskStatus.Pending, string? error = null,
        string mode = TranscriptTaskModes.File, string source = TranscriptTaskSources.Manual)
    {
        var now = DateTime.UtcNow;
        var task = new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = roomId,
            File = "a.flv",
            ModelId = modelId,
            Type = TranscriptTaskTypes.Recording,
            Mode = mode,
            Source = source,
            Status = status,
            Error = error,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Tasks.InsertAsync(task);
        return task;
    }

    [Fact]
    public async Task ListRecordings_ReturnsArchiveItems()
    {
        await SeedRecordingAsync(700001, 111, "场次一", "[RAW] a.flv", "[AV1] a.flv");
        await SeedRecordingAsync(700002, 222, "场次二", "[RAW] b.flv");

        var json = Ok(await CreateArchiveController().ListRecordings(roomId: 700001));

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal(700001, item.GetProperty("roomId").GetInt64());
        Assert.Equal(111, item.GetProperty("mid").GetInt64());
        Assert.Equal("场次一", item.GetProperty("title").GetString());
        Assert.Equal(2, item.GetProperty("files").GetArrayLength());
        Assert.True(item.GetProperty("startedAt").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("endedAt").ValueKind);
    }

    [Fact]
    public async Task ListRecordings_ClampsRequestedLimitToConfiguredMaxPageSize()
    {
        await SeedRecordingAsync(700003, 111, "场次一", "[RAW] a.flv");
        await SeedRecordingAsync(700004, 111, "场次二", "[RAW] b.flv");

        var options = Options.Create(new TranscriptApiOptions { MaxPageSize = 1 });
        var json = Ok(await CreateArchiveController(options).ListRecordings(roomId: 700003, limit: 200));

        Assert.Single(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GetRoomTranscripts_ReturnsTasksAndAutoEnqueueFlag()
    {
        var recording = await SeedRecordingAsync(700010, 333, "晚间歌回", "[RAW] a.flv");
        var model = await SeedModelAsync("qwen-fun-asr");
        await SeedTaskAsync(recording.Id, model.Id, 700010, TranscriptTaskStatus.Running, mode: TranscriptTaskModes.Live, source: TranscriptTaskSources.Auto);
        await AutoEnqueue.UpsertAsync(700010, 333, enabled: true);

        var json = Ok(await CreateArchiveController().GetRoomTranscripts(700010));

        Assert.Equal(700010, json.GetProperty("roomId").GetInt64());
        Assert.True(json.GetProperty("autoEnqueueEnabled").GetBoolean());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(recording.Id.ToString(), item.GetProperty("recordingId").GetString());
        var task = Assert.Single(item.GetProperty("tasks").EnumerateArray());
        Assert.Equal("qwen-fun-asr", task.GetProperty("modelName").GetString());
        Assert.Equal("RUNNING", task.GetProperty("status").GetString());
        Assert.Equal("auto", task.GetProperty("source").GetString());
    }

    [Fact]
    public async Task UpdateSettings_UpsertsWithResolvedMid()
    {
        await SeedRecordingAsync(700020, 444, "场次", "[RAW] a.flv");

        var result = await CreateArchiveController()
            .UpdateSettings(700020, new TranscriptSettingsUpdateRequest(true));

        var json = Ok(result);
        Assert.Equal(700020, json.GetProperty("roomId").GetInt64());
        Assert.True(json.GetProperty("enabled").GetBoolean());

        var stored = await AutoEnqueue.GetByRoomIdAsync(700020);
        Assert.NotNull(stored);
        Assert.Equal(444, stored!.Mid);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public async Task Enqueue_MissingRecordingOrModel_Returns404()
    {
        var controller = CreateTasksController();
        var missing = ObjectId.GenerateNewId().ToString();

        Assert.IsType<NotFoundObjectResult>(
            await controller.Enqueue(new EnqueueRequest(missing, missing)));

        var recording = await SeedEndedRecordingAsync(700030, 555, "场次", "[RAW] a.flv");
        Assert.IsType<NotFoundObjectResult>(
            await controller.Enqueue(new EnqueueRequest(recording.Id.ToString(), missing)));
    }

    [Fact]
    public async Task Enqueue_DisabledModel_Returns404()
    {
        var recording = await SeedEndedRecordingAsync(700031, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync(enabled: false);

        var result = await CreateTasksController()
            .Enqueue(new EnqueueRequest(recording.Id.ToString(), model.Id.ToString()));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Enqueue_NotEndedRecording_Returns400()
    {
        var recording = await SeedRecordingAsync(700034, 555, "进行中场次", "[RAW] a.flv");
        var model = await SeedModelAsync();

        var result = await CreateTasksController()
            .Enqueue(new EnqueueRequest(recording.Id.ToString(), model.Id.ToString()));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(badRequest.Value);
        Assert.Equal("recording has not ended", json.GetProperty("error").GetString());

        Assert.Empty(await Tasks.ListAsync(recording.Id, null, null, 10));
    }

    [Fact]
    public async Task Enqueue_CreatesPendingFileTaskPreferringRaw()
    {
        var recording = await SeedEndedRecordingAsync(700032, 555, "场次", "[AV1] a.flv", "[RAW] a.flv");
        var model = await SeedModelAsync();

        var json = Ok(await CreateTasksController()
            .Enqueue(new EnqueueRequest(recording.Id.ToString(), model.Id.ToString())));

        Assert.Equal("PENDING", json.GetProperty("status").GetString());
        var taskId = ObjectId.Parse(json.GetProperty("taskId").GetString());

        var stored = await Tasks.GetByIdAsync(taskId);
        Assert.NotNull(stored);
        Assert.Equal(TranscriptTaskModes.File, stored!.Mode);
        Assert.Equal(TranscriptTaskSources.Manual, stored.Source);
        Assert.Equal("[RAW] a.flv", stored.File);
    }

    [Fact]
    public async Task Enqueue_Duplicate_Returns409WithExistingTaskId()
    {
        var recording = await SeedEndedRecordingAsync(700033, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var controller = CreateTasksController();

        var first = Ok(await controller.Enqueue(new EnqueueRequest(recording.Id.ToString(), model.Id.ToString())));
        var second = await controller.Enqueue(new EnqueueRequest(recording.Id.ToString(), model.Id.ToString()));

        var conflict = Assert.IsType<ConflictObjectResult>(second);
        var json = JsonSerializer.SerializeToElement(conflict.Value);
        Assert.Equal(first.GetProperty("taskId").GetString(), json.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Retry_FailedTask_SetsRunningAndClearsError()
    {
        var recording = await SeedRecordingAsync(700040, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var task = await SeedTaskAsync(recording.Id, model.Id, 700040,
            TranscriptTaskStatus.Failed, error: "boom");

        var json = Ok(await CreateTasksController().RetryTask(task.Id.ToString()));

        Assert.Equal("RUNNING", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TranscriptTaskStatus.Running, stored!.Status);
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task Retry_NonFailedTask_Returns409()
    {
        var recording = await SeedRecordingAsync(700041, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var task = await SeedTaskAsync(recording.Id, model.Id, 700041, TranscriptTaskStatus.Pending);

        var result = await CreateTasksController().RetryTask(task.Id.ToString());

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Retry_UnknownTask_Returns404()
    {
        var result = await CreateTasksController()
            .RetryTask(ObjectId.GenerateNewId().ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetTask_ReturnsSegmentsOrderedByStart()
    {
        var recording = await SeedRecordingAsync(700050, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var task = await SeedTaskAsync(recording.Id, model.Id, 700050);
        await Segments.InsertAsync(new TranscriptSegmentRecord { TaskId = task.Id, StartTime = 3000, EndTime = 3500, Text = "c" });
        await Segments.InsertAsync(new TranscriptSegmentRecord { TaskId = task.Id, StartTime = 1000, EndTime = 1500, Text = "a" });
        await Segments.InsertAsync(new TranscriptSegmentRecord { TaskId = task.Id, StartTime = 2000, EndTime = 2500, Text = "b" });

        var json = Ok(await CreateTasksController().GetTask(task.Id.ToString()));

        Assert.Equal(task.Id.ToString(), json.GetProperty("task").GetProperty("id").GetString());
        Assert.Equal("qwen-fun-asr", json.GetProperty("task").GetProperty("modelName").GetString());
        Assert.Equal("场次", json.GetProperty("task").GetProperty("title").GetString());

        var segments = json.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal(3, segments.Length);
        Assert.Equal(1000, segments[0].GetProperty("start").GetInt64());
        Assert.Equal(1500, segments[0].GetProperty("end").GetInt64());
        Assert.Equal("a", segments[0].GetProperty("text").GetString());
        Assert.Equal(3000, segments[2].GetProperty("start").GetInt64());
    }

    [Fact]
    public async Task GetTask_Unknown_Returns404()
    {
        Assert.IsType<NotFoundObjectResult>(
            await CreateTasksController().GetTask(ObjectId.GenerateNewId().ToString()));
    }

    [Fact]
    public async Task ListTasks_FiltersByStatusAndRoom()
    {
        var recordingA = await SeedRecordingAsync(700060, 555, "场次A", "[RAW] a.flv");
        var recordingB = await SeedRecordingAsync(700061, 556, "场次B", "[RAW] b.flv");
        var model = await SeedModelAsync();
        await SeedTaskAsync(recordingA.Id, model.Id, 700060, TranscriptTaskStatus.Failed);
        await SeedTaskAsync(recordingB.Id, model.Id, 700061, TranscriptTaskStatus.Done);

        var json = Ok(await CreateTasksController().ListTasks(status: "FAILED"));
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal(recordingA.Id.ToString(), item.GetProperty("recordingId").GetString());

        var byRoom = Ok(await CreateTasksController().ListTasks(roomId: 700061));
        var roomItem = Assert.Single(byRoom.GetProperty("items").EnumerateArray());
        Assert.Equal("DONE", roomItem.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Models_NeverExposeApiKey_AndDefaultSwitchIsAtomic()
    {
        var controller = CreateModelsController();

        var first = Ok(await controller.Create(new TranscriptModelCreateRequest(
            "model-a", "ali-fun-asr", "streaming", "key-a", true, true, 2)));
        var firstId = ObjectId.Parse(first.GetProperty("id").GetString());
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.False(first.TryGetProperty("apiKey", out _));

        var second = Ok(await controller.Create(new TranscriptModelCreateRequest(
            "model-b", "ali-fun-asr", "streaming", "key-b", true, true, 4)));
        var secondId = ObjectId.Parse(second.GetProperty("id").GetString());

        var list = Ok(await controller.List());
        var items = list.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, i => Assert.False(i.TryGetProperty("apiKey", out _)));

        var storedFirst = await Models.GetByIdAsync(firstId);
        var storedSecond = await Models.GetByIdAsync(secondId);
        Assert.False(storedFirst!.IsDefault);
        Assert.True(storedSecond!.IsDefault);

        var defaultModel = await Models.GetDefaultAsync();
        Assert.Equal(secondId, defaultModel!.Id);
    }

    [Fact]
    public async Task Models_UpdateTogglesEnabledAndDefaultAndKeepsApiKey()
    {
        var controller = CreateModelsController();
        var first = Ok(await controller.Create(new TranscriptModelCreateRequest(
            "model-a", "ali-fun-asr", "streaming", "key-a", true, true, 2)));
        var second = Ok(await controller.Create(new TranscriptModelCreateRequest(
            "model-b", "ali-fun-asr", "streaming", "key-b", true, false, 1)));
        var firstId = first.GetProperty("id").GetString()!;
        var secondId = second.GetProperty("id").GetString()!;

        var updated = Ok(await controller.Update(secondId,
            new TranscriptModelUpdateRequest(Enabled: false, IsDefault: true, MaxConcurrency: 7, ApiKey: null)));
        Assert.False(updated.GetProperty("enabled").GetBoolean());
        Assert.True(updated.GetProperty("isDefault").GetBoolean());
        Assert.Equal(7, updated.GetProperty("maxConcurrency").GetInt32());
        Assert.False(updated.TryGetProperty("apiKey", out _));

        Assert.False((await Models.GetByIdAsync(ObjectId.Parse(firstId)))!.IsDefault);
        Assert.Equal("key-b", (await Models.GetByIdAsync(ObjectId.Parse(secondId)))!.ApiKey);
    }

    [Fact]
    public async Task Models_CreateWithoutRequiredFields_Returns400()
    {
        var controller = CreateModelsController();

        Assert.IsType<BadRequestObjectResult>(await controller.Create(
            new TranscriptModelCreateRequest(null, "ali-fun-asr", "streaming", "key", true, false, 1)));
        Assert.IsType<BadRequestObjectResult>(await controller.Create(
            new TranscriptModelCreateRequest("model", "ali-fun-asr", "streaming", null, true, false, 1)));
    }

    [Fact]
    public async Task Settings_ListJoinsAnchorName()
    {
        await AutoEnqueue.UpsertAsync(700070, 777, enabled: true);
        await AccountInfo.UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = 777,
            RoomId = 700070,
            Data = """{"uName":"某主播"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateSettingsController().ListSettings());

        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(700070, item.GetProperty("roomId").GetInt64());
        Assert.Equal(777, item.GetProperty("mid").GetInt64());
        Assert.Equal("某主播", item.GetProperty("anchorName").GetString());
        Assert.True(item.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Sse_ReplaysLiveDedupesAndClosesOnDone()
    {
        var recording = await SeedRecordingAsync(700080, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var task = await SeedTaskAsync(recording.Id, model.Id, 700080, TranscriptTaskStatus.Running);
        await Segments.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = task.Id,
            StartTime = 1000,
            EndTime = 1500,
            Text = "replay-segment",
        });

        var stream = new TranscriptSegmentStream(
            Segments, Tasks, redis.Connection, NullLogger<TranscriptSegmentStream>.Instance);

        using var body = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = body } };

        var originalInterval = TranscriptSegmentStream.HeartbeatInterval;
        TranscriptSegmentStream.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var streamTask = stream.StreamAsync(context.Response, task.Id, cts.Token);

            await Task.Delay(300);

            var publisher = redis.Connection.GetSubscriber();
            var channel = new RedisChannel(
                RecordingEventChannels.SegmentChannel(task.Id.ToString()),
                RedisChannel.PatternMode.Literal);
            await publisher.PublishAsync(channel, JsonSerializer.Serialize(
                new TranscriptSegmentEvent(task.Id.ToString(), 2000, 2500, "live-segment")));
            await publisher.PublishAsync(channel, JsonSerializer.Serialize(
                new TranscriptSegmentEvent(task.Id.ToString(), 1000, 1500, "duplicate-segment")));

            await Task.Delay(300);
            await Tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Done);

            var completed = await Task.WhenAny(streamTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(streamTask, completed);
            await streamTask;
        }
        finally
        {
            TranscriptSegmentStream.HeartbeatInterval = originalInterval;
        }

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        Assert.StartsWith("data: ", content);
        Assert.Contains("replay-segment", content);
        Assert.Contains("live-segment", content);
        Assert.DoesNotContain("duplicate-segment", content);
        Assert.Contains("event: done", content);
        Assert.Contains(": heartbeat", content);
        Assert.Equal(1, CountOccurrences(content, "\"start\":1000"));
        Assert.Equal(1, CountOccurrences(content, "\"start\":2000"));
    }

    [Fact]
    public async Task Sse_SubscribesBeforeReplay_SegmentPublishedDuringReplayIsDeliveredOnce()
    {
        var recording = await SeedRecordingAsync(700082, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var task = await SeedTaskAsync(recording.Id, model.Id, 700082, TranscriptTaskStatus.Running);
        await Segments.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = task.Id,
            StartTime = 1000,
            EndTime = 1500,
            Text = "replay-segment",
        });

        var stream = new PublishesDuringReplayStream(
            Segments,
            Tasks,
            redis.Connection,
            NullLogger<TranscriptSegmentStream>.Instance,
            new TranscriptSegmentEvent(task.Id.ToString(), 4000, 4500, "during-replay"));

        using var body = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = body } };

        var originalInterval = TranscriptSegmentStream.HeartbeatInterval;
        TranscriptSegmentStream.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var streamTask = stream.StreamAsync(context.Response, task.Id, cts.Token);

            await Task.Delay(400);
            await Tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Done);

            var completed = await Task.WhenAny(streamTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(streamTask, completed);
            await streamTask;
        }
        finally
        {
            TranscriptSegmentStream.HeartbeatInterval = originalInterval;
        }

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        Assert.Contains("replay-segment", content);
        Assert.Contains("during-replay", content);
        Assert.Equal(1, CountOccurrences(content, "\"start\":4000"));
        Assert.Contains("event: done", content);
    }

    [Fact]
    public async Task Sse_AlreadyTerminalTask_ReplaysThenDone()
    {
        var recording = await SeedRecordingAsync(700081, 555, "场次", "[RAW] a.flv");
        var model = await SeedModelAsync();
        var task = await SeedTaskAsync(recording.Id, model.Id, 700081, TranscriptTaskStatus.Done);
        await Segments.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = task.Id,
            StartTime = 500,
            EndTime = 900,
            Text = "final",
        });

        var stream = new TranscriptSegmentStream(
            Segments, Tasks, redis.Connection, NullLogger<TranscriptSegmentStream>.Instance);
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = body } };

        await stream.StreamAsync(context.Response, task.Id, CancellationToken.None);

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        Assert.Contains("final", content);
        Assert.Contains("event: done", content);
        Assert.Contains("DONE", content);
    }

    [Fact]
    public async Task Sse_UnknownTask_Returns404()
    {
        var result = await CreateTasksController().StreamTask(ObjectId.GenerateNewId().ToString(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    private sealed class PublishesDuringReplayStream : TranscriptSegmentStream
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly TranscriptSegmentEvent _concurrent;

        public PublishesDuringReplayStream(
            TranscriptSegmentRepository segments,
            TranscriptTaskRepository tasks,
            IConnectionMultiplexer redis,
            ILogger<TranscriptSegmentStream> logger,
            TranscriptSegmentEvent concurrent)
            : base(segments, tasks, redis, logger)
        {
            _redis = redis;
            _concurrent = concurrent;
        }

        protected override async Task<IReadOnlyList<TranscriptSegmentRecord>> ReplaySegmentsAsync(
            ObjectId taskId, CancellationToken ct)
        {
            var channel = new RedisChannel(
                RecordingEventChannels.SegmentChannel(taskId.ToString()),
                RedisChannel.PatternMode.Literal);
            await _redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(_concurrent));
            return await base.ReplaySegmentsAsync(taskId, ct);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
