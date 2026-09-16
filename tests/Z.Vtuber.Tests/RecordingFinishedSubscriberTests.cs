using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;
using Z.Vtuber.RecordingTranscript.Consumer.Events;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RecordingFinishedSubscriberTests(MongoDbFixture mongo, RedisFixture redis)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);

    private TranscriptTaskRepository Tasks => new(mongo.Database);

    private TranscriptModelRepository Models => new(mongo.Database);

    private TranscriptAutoEnqueueRepository AutoEnqueue => new(mongo.Database);

    private TranscriptEnqueueService Enqueue => new(
        Recordings, Tasks, Models, AutoEnqueue, NullLogger<TranscriptEnqueueService>.Instance);

    private RecordingFinishedSubscriber Subscriber => new(
        redis.Connection, Enqueue, NullLogger<RecordingFinishedSubscriber>.Instance);

    private static async Task<ObjectId> SeedDefaultModelAsync(TranscriptModelRepository models) =>
        await models.InsertAsync(new TranscriptModelRecord
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

    private static async Task<ObjectId> SeedEndedRecordingAsync(
        RecordingRepository recordings, long roomId, DateTime endedAt) =>
        await recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = ["[RAW]a.flv", "[AV1]b.flv"],
            StartedAt = endedAt.AddHours(-1),
            EndedAt = endedAt,
            CreatedAt = endedAt.AddHours(-1),
        });

    [Fact]
    public async Task Finished_MissingLiveTask_CreatesFileAutoTask()
    {
        var modelId = await SeedDefaultModelAsync(Models);
        var enabledAt = DateTime.UtcNow.AddMinutes(-5);
        await AutoEnqueue.UpsertAsync(400, 401, enabled: true);
        var endedAt = DateTime.UtcNow;
        var recordingId = await SeedEndedRecordingAsync(Recordings, 400, endedAt);

        var evt = new RecordingFinishedEvent(
            recordingId.ToString(), 400, 401, "title", ["[RAW]a.flv", "[AV1]b.flv"], [], endedAt);
        await Subscriber.HandleMessageAsync(JsonSerializer.Serialize(evt));

        var task = Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
        Assert.Equal(TranscriptTaskModes.File, task.Mode);
        Assert.Equal(TranscriptTaskSources.Auto, task.Source);
        Assert.Equal(modelId, task.ModelId);
        Assert.Equal("[RAW]a.flv", task.File);
    }

    [Fact]
    public async Task Finished_LiveTaskExists_DoesNotCreateDuplicate()
    {
        await SeedDefaultModelAsync(Models);
        await AutoEnqueue.UpsertAsync(402, 403, enabled: true);
        var endedAt = DateTime.UtcNow;
        var recordingId = await SeedEndedRecordingAsync(Recordings, 402, endedAt);

        await Tasks.InsertAsync(new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = 402,
            File = "[RAW]a.flv",
            ModelId = (await Models.GetDefaultAsync())!.Id,
            Mode = TranscriptTaskModes.Live,
            Source = TranscriptTaskSources.Auto,
            Status = TranscriptTaskStatus.Done,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var evt = new RecordingFinishedEvent(
            recordingId.ToString(), 402, 403, "title", ["[RAW]a.flv"], [], endedAt);
        await Subscriber.HandleMessageAsync(JsonSerializer.Serialize(evt));

        var task = Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
        Assert.Equal(TranscriptTaskModes.Live, task.Mode);
    }

    [Fact]
    public async Task Finished_Disabled_DoesNotCreateTask()
    {
        await SeedDefaultModelAsync(Models);
        await AutoEnqueue.UpsertAsync(404, 405, enabled: false);
        var endedAt = DateTime.UtcNow;
        var recordingId = await SeedEndedRecordingAsync(Recordings, 404, endedAt);

        var evt = new RecordingFinishedEvent(
            recordingId.ToString(), 404, 405, "title", ["[RAW]a.flv"], [], endedAt);
        await Subscriber.HandleMessageAsync(JsonSerializer.Serialize(evt));

        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Finished_EndedBeforeEnabledAt_DoesNotBackfill()
    {
        await SeedDefaultModelAsync(Models);
        await AutoEnqueue.UpsertAsync(406, 407, enabled: true);
        var endedAt = DateTime.UtcNow.AddHours(-2);
        var recordingId = await SeedEndedRecordingAsync(Recordings, 406, endedAt);

        var evt = new RecordingFinishedEvent(
            recordingId.ToString(), 406, 407, "title", ["[RAW]a.flv"], [], endedAt);
        await Subscriber.HandleMessageAsync(JsonSerializer.Serialize(evt));

        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Finished_InvalidJson_DoesNotThrow()
    {
        await Subscriber.HandleMessageAsync("{}");
        await Subscriber.HandleMessageAsync("not-json");
    }
}
