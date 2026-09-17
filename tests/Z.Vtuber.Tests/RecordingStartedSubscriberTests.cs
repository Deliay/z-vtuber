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
public class RecordingStartedSubscriberTests(MongoDbFixture mongo, RedisFixture redis)
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

    private RecordingStartedSubscriber Subscriber => new(
        redis.Connection, Enqueue, NullLogger<RecordingStartedSubscriber>.Instance);

    private static async Task<ObjectId> SeedDefaultModelAsync(TranscriptModelRepository models) =>
        await models.InsertAsync(new TranscriptModelRecord
        {
            Name = "default",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key",
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = 2,
            CreatedAt = DateTime.UtcNow,
        });

    private static async Task<ObjectId> SeedRecordingAsync(RecordingRepository recordings, long roomId) =>
        await recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = ["[RAW]a.flv"],
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task Started_Enabled_CreatesLiveAutoTask()
    {
        var modelId = await SeedDefaultModelAsync(Models);
        await AutoEnqueue.UpsertAsync(300, 301, enabled: true, modelId);
        var recordingId = await SeedRecordingAsync(Recordings, 300);

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 300, 301, "title", "http://rec/stream/audio", DateTime.UtcNow);
        await Subscriber.HandleMessageAsync(JsonSerializer.Serialize(evt));

        var task = Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
        Assert.Equal(TranscriptTaskModes.Live, task.Mode);
        Assert.Equal(TranscriptTaskSources.Auto, task.Source);
        Assert.Equal(modelId, task.ModelId);
    }

    [Fact]
    public async Task Started_Disabled_DoesNotCreateTask()
    {
        await SeedDefaultModelAsync(Models);
        await AutoEnqueue.UpsertAsync(302, 303, enabled: false);
        var recordingId = await SeedRecordingAsync(Recordings, 302);

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 302, 303, "title", "http://rec/stream/audio", DateTime.UtcNow);
        await Subscriber.HandleMessageAsync(JsonSerializer.Serialize(evt));

        Assert.Empty(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Started_Duplicate_IsIdempotent()
    {
        var modelId = await SeedDefaultModelAsync(Models);
        await AutoEnqueue.UpsertAsync(304, 305, enabled: true, modelId);
        var recordingId = await SeedRecordingAsync(Recordings, 304);

        var evt = new RecordingStartedEvent(
            recordingId.ToString(), 304, 305, "title", "http://rec/stream/audio", DateTime.UtcNow);
        var json = JsonSerializer.Serialize(evt);
        await Subscriber.HandleMessageAsync(json);
        await Subscriber.HandleMessageAsync(json);

        Assert.Single(await Tasks.ListAsync(recordingId, null, null, 10));
    }

    [Fact]
    public async Task Started_InvalidJson_DoesNotThrow()
    {
        await Subscriber.HandleMessageAsync("not-json");
    }
}
