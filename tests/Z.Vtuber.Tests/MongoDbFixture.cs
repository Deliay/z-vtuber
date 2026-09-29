using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

public sealed class MongoDbFixture : IAsyncLifetime
{
    private static readonly MongoDbContainer SharedContainer = new MongoDbBuilder("mongo:7.0")
        .WithEntrypoint("sh", "-c", "ulimit -n 65535 && exec docker-entrypoint.sh mongod")
        .Build();

    private static int _counter;
    private readonly string _databaseName = $"test_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await SharedContainer.StartAsync();
        var client = new MongoClient(SharedContainer.GetConnectionString());
        Database = client.GetDatabase(_databaseName);

        await ResetAsync();
    }

    public async Task ResetAsync()
    {
        var names = new[]
        {
            CookieCollectionNames.Cookies,
            CookieCollectionNames.CookieSessions,
            LiveCollectionNames.Subscriptions,
            LiveCollectionNames.AccountBasicInfo,
            LiveCollectionNames.LiveStatus,
            LiveCollectionNames.LiveEvent,
            LiveCollectionNames.SpaceTimeline,
            LiveCollectionNames.LiveSession,
            CoreCollectionNames.Meta,
            LiveCollectionNames.LiveEventTextIndex,
            CookieCollectionNames.CookieAccountInfo,
            LiveCollectionNames.LiveGuards,
            WeiboCollectionNames.WeiboSubscriptions,
            WeiboCollectionNames.WeiboUserTimeline,
            CookieCollectionNames.WeiboCookies,
            CookieCollectionNames.WeiboCookieSessions,
            NotificationCollectionNames.NotificationSubscriptions,
            RecordingCollectionNames.RecordingStatus,
            RecordingCollectionNames.Recordings,
            TranscriptCollectionNames.TranscriptTasks,
            TranscriptCollectionNames.TranscriptSegments,
            TranscriptCollectionNames.TranscriptModels,
            TranscriptCollectionNames.TranscriptAutoEnqueue,
            TrainingCollectionNames.TrainingDatasets,
            TrainingCollectionNames.TrainingDatasetItems,
            TrainingCollectionNames.TrainingProviders,
            TrainingCollectionNames.TrainingModels,
            TrainingCollectionNames.TrainingEmotionTasks,
        };

        foreach (var name in names)
        {
            await Database.DropCollectionAsync(name);
        }

        var live = new LiveMongoSetup(Database, NullLogger<LiveMongoSetup>.Instance);
        var cookie = new CookieMongoSetup(Database, NullLogger<CookieMongoSetup>.Instance);
        var weibo = new WeiboMongoSetup(Database, NullLogger<WeiboMongoSetup>.Instance);
        var recording = new RecordingMongoSetup(Database, NullLogger<RecordingMongoSetup>.Instance);
        var transcript = new TranscriptMongoSetup(Database, NullLogger<TranscriptMongoSetup>.Instance);
        var training = new TrainingMongoSetup(Database, NullLogger<TrainingMongoSetup>.Instance);
        var notification = new NotificationMongoSetup(Database, NullLogger<NotificationMongoSetup>.Instance);

        await live.EnsureIndexesAsync();
        await cookie.EnsureIndexesAsync();
        await weibo.EnsureIndexesAsync();
        await recording.EnsureIndexesAsync();
        await transcript.EnsureIndexesAsync();
        await training.EnsureIndexesAsync();
        await notification.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
