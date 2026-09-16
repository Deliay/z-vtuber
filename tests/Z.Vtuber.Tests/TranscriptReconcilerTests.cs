using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;
using Z.Vtuber.RecordingTranscript.Consumer.Reconcile;

namespace Z.Vtuber.Tests;

public class TranscriptReconcilerTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);

    private TranscriptTaskRepository Tasks => new(mongo.Database);

    private TranscriptModelRepository Models => new(mongo.Database);

    private TranscriptAutoEnqueueRepository AutoEnqueue => new(mongo.Database);

    private RecordingStatusRepository Statuses => new(mongo.Database);

    private TranscriptEnqueueService Enqueue => new(
        Recordings, Tasks, Models, AutoEnqueue, NullLogger<TranscriptEnqueueService>.Instance);

    private TranscriptReconciler Reconciler => new(
        Statuses, Recordings, AutoEnqueue, Models, Tasks, Enqueue,
        new TranscriptConsumerOptions(), NullLogger<TranscriptReconciler>.Instance);

    private async Task<ObjectId> InsertModelAsync() =>
        await Models.InsertAsync(new TranscriptModelRecord
        {
            Name = "default",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key",
            Enabled = true,
            IsDefault = true,
            MaxConcurrency = 2,
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<ObjectId> InsertRecordingAsync(long roomId, DateTime startedAt, DateTime? endedAt) =>
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = ["[RAW]a.flv"],
            StartedAt = startedAt,
            EndedAt = endedAt,
            CreatedAt = startedAt,
        });

    private Task SetRecordingStatusAsync(long roomId) =>
        Statuses.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = roomId,
            State = "recording",
            HostName = "host",
            SessionTitle = "title",
            Files = ["[RAW]a.flv"],
            UpdatedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task InProgress_MissingLiveTask_CreatesLiveTask()
    {
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(500, 501, enabled: true);
        await SetRecordingStatusAsync(500);
        var recordingId = await InsertRecordingAsync(500, DateTime.UtcNow, endedAt: null);

        await Reconciler.ReconcileOnceAsync();

        var task = Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
        Assert.Equal(TranscriptTaskModes.Live, task.Mode);
        Assert.Equal(TranscriptTaskSources.Auto, task.Source);
    }

    [Fact]
    public async Task Ended_MissingFileTask_CreatesFileTask()
    {
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(510, 511, enabled: true);
        var recordingId = await InsertRecordingAsync(510, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);

        await Reconciler.ReconcileOnceAsync();

        var task = Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
        Assert.Equal(TranscriptTaskModes.File, task.Mode);
    }

    [Fact]
    public async Task Historical_NotBackfilled()
    {
        var recordingId = await InsertRecordingAsync(520, DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-2));
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(520, 521, enabled: true);

        await Reconciler.ReconcileOnceAsync();

        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Disabled_DoesNotCreateTask()
    {
        var recordingId = await InsertRecordingAsync(530, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(530, 531, enabled: false);

        await Reconciler.ReconcileOnceAsync();

        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Idempotent_RepeatedReconcile_CreatesSingleTask()
    {
        await InsertModelAsync();
        await AutoEnqueue.UpsertAsync(540, 541, enabled: true);
        var recordingId = await InsertRecordingAsync(540, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);

        await Reconciler.ReconcileOnceAsync();
        await Reconciler.ReconcileOnceAsync();

        Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task NoDefaultModel_DoesNotCreateTask()
    {
        var recordingId = await InsertRecordingAsync(550, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
        await AutoEnqueue.UpsertAsync(550, 551, enabled: true);

        await Reconciler.ReconcileOnceAsync();

        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }
}
