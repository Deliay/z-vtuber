using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;

namespace Z.Vtuber.Tests;

public class TranscriptEnqueueTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(fixture.Database);

    private TranscriptTaskRepository Tasks => new(fixture.Database);

    private TranscriptModelRepository Models => new(fixture.Database);

    private TranscriptAutoEnqueueRepository AutoEnqueue => new(fixture.Database);

    private TranscriptEnqueueService Enqueue => new(
        Recordings,
        Tasks,
        Models,
        AutoEnqueue,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<TranscriptEnqueueService>.Instance);

    private async Task<ObjectId> InsertModelAsync(bool enabled = true, string name = "model")
    {
        var id = await Models.InsertAsync(new TranscriptModelRecord
        {
            Name = name,
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key-" + name,
            Model = "fun-asr-realtime",
            Enabled = enabled,
            MaxConcurrency = 2,
            CreatedAt = DateTime.UtcNow,
        });
        return id;
    }

    private async Task<ObjectId> InsertRecordingAsync(
        long roomId, params string[] files) =>
        await InsertRecordingAsync(roomId, DateTime.UtcNow, files);

    private async Task<ObjectId> InsertRecordingAsync(
        long roomId, DateTime endedAt, params string[] files) =>
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = [.. files],
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = endedAt,
            CreatedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task Manual_CreatesFileManualTask()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(100, "[RAW]a.flv");

        var result = await Enqueue.EnqueueManualAsync(recordingId, modelId);

        Assert.Equal(EnqueueError.None, result.Error);
        var task = await Tasks.GetByIdAsync(result.TaskId!.Value);
        Assert.NotNull(task);
        Assert.Equal(TranscriptTaskModes.File, task!.Mode);
        Assert.Equal(TranscriptTaskSources.Manual, task.Source);
        Assert.Equal(recordingId, task.RecordingId);
        Assert.Equal(modelId, task.ModelId);
    }

    [Fact]
    public async Task Manual_SameRecordingAndModel_IsIdempotent()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(101, "a.flv");

        var first = await Enqueue.EnqueueManualAsync(recordingId, modelId);
        var second = await Enqueue.EnqueueManualAsync(recordingId, modelId);

        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Manual_MissingRecording_ReturnsError()
    {
        var modelId = await InsertModelAsync();

        var result = await Enqueue.EnqueueManualAsync(ObjectId.GenerateNewId(), modelId);

        Assert.Equal(EnqueueError.RecordingNotFound, result.Error);
        Assert.Null(result.TaskId);
    }

    [Fact]
    public async Task Manual_MissingModel_ReturnsError()
    {
        var recordingId = await InsertRecordingAsync(102, "a.flv");

        var result = await Enqueue.EnqueueManualAsync(recordingId, ObjectId.GenerateNewId());

        Assert.Equal(EnqueueError.ModelNotFound, result.Error);
    }

    [Fact]
    public async Task Manual_DisabledModel_ReturnsError()
    {
        var modelId = await InsertModelAsync(enabled: false);
        var recordingId = await InsertRecordingAsync(103, "a.flv");

        var result = await Enqueue.EnqueueManualAsync(recordingId, modelId);

        Assert.Equal(EnqueueError.ModelDisabled, result.Error);
        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Started_EnabledWithBoundModel_CreatesLiveAutoTask()
    {
        var boundModelId = await InsertModelAsync(name: "bound");
        await InsertModelAsync(name: "other");
        await AutoEnqueue.UpsertAsync(200, 201, enabled: true, boundModelId);
        var recordingId = await InsertRecordingAsync(200, "[RAW]a.flv", "[AV1]b.flv");

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 200, 201, "title", "http://rec/stream/audio", DateTime.UtcNow);
        var taskId = await Enqueue.EnqueueForStartedAsync(evt);

        Assert.NotNull(taskId);
        var task = await Tasks.GetByIdAsync(taskId!.Value);
        Assert.NotNull(task);
        Assert.Equal(TranscriptTaskModes.Live, task!.Mode);
        Assert.Equal(TranscriptTaskSources.Auto, task.Source);
        Assert.Equal(boundModelId, task.ModelId);
        Assert.Equal("[RAW]a.flv", task.File);
    }

    [Fact]
    public async Task Started_Disabled_DoesNotCreateTask()
    {
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(201, 202, enabled: false);
        var recordingId = await InsertRecordingAsync(201, "a.flv");

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 201, 202, "title", "http://rec/stream/audio", DateTime.UtcNow);
        var taskId = await Enqueue.EnqueueForStartedAsync(evt);

        Assert.Null(taskId);
        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Started_NoSetting_DoesNotCreateTask()
    {
        await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(202, "a.flv");

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 202, 203, "title", "http://rec/stream/audio", DateTime.UtcNow);
        var taskId = await Enqueue.EnqueueForStartedAsync(evt);

        Assert.Null(taskId);
    }

    [Fact]
    public async Task Started_NoBoundModel_DoesNotCreateTask()
    {
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(204, 205, enabled: true);
        var recordingId = await InsertRecordingAsync(204, "a.flv");

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 204, 205, "title", "http://rec/stream/audio", DateTime.UtcNow);
        var taskId = await Enqueue.EnqueueForStartedAsync(evt);

        Assert.Null(taskId);
        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Finished_DisabledBoundModel_DoesNotCreateTask()
    {
        var modelId = await InsertModelAsync(enabled: false);
        await AutoEnqueue.UpsertAsync(208, 209, enabled: true, modelId);
        var recordingId = await InsertRecordingAsync(208, DateTime.UtcNow.AddMinutes(5), "a.flv");
        var recording = await Recordings.GetByIdAsync(recordingId);
        var evt = new RecordingFinishedEvent(
            recordingId.ToString(), 208, 209, "title", recording!.Files.ToArray(), [], recording.EndedAt!.Value);

        var taskId = await Enqueue.EnqueueForFinishedAsync(evt);

        Assert.Null(taskId);
        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Started_IsIdempotent()
    {
        var modelId = await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(206, 207, enabled: true, modelId);
        var recordingId = await InsertRecordingAsync(206, "a.flv");

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 206, 207, "title", "http://rec/stream/audio", DateTime.UtcNow);
        var first = await Enqueue.EnqueueForStartedAsync(evt);
        var second = await Enqueue.EnqueueForStartedAsync(evt);

        Assert.Equal(first, second);
        Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Theory]
    [InlineData(new[] { "[AV1]a.flv", "[RAW]b.flv" }, "[RAW]b.flv")]
    [InlineData(new[] { "[AV1]a.flv", "[AV1]b.flv" }, "[AV1]a.flv")]
    [InlineData(new[] { "[raw]lower.flv" }, "[raw]lower.flv")]
    public void PickFile_PrefersRawElseFirst(string[] files, string expected)
    {
        Assert.Equal(expected, TranscriptEnqueueService.PickFile(files));
    }

    [Fact]
    public void PickFile_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TranscriptEnqueueService.PickFile([]));
    }
}
