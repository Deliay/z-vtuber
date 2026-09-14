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
            MongoCollectionNames.Cookies,
            MongoCollectionNames.CookieSessions,
            MongoCollectionNames.Subscriptions,
            MongoCollectionNames.AccountBasicInfo,
            MongoCollectionNames.LiveStatus,
            MongoCollectionNames.LiveEvent,
            MongoCollectionNames.SpaceTimeline,
            MongoCollectionNames.LiveSession,
            MongoCollectionNames.Meta,
            MongoCollectionNames.LiveEventTextIndex,
            MongoCollectionNames.CookieAccountInfo,
            MongoCollectionNames.LiveGuards,
            MongoCollectionNames.WeiboSubscriptions,
            MongoCollectionNames.WeiboUserTimeline,
            MongoCollectionNames.NotificationSubscriptions,
            MongoCollectionNames.RecordingStatus,
        };

        foreach (var name in names)
        {
            await Database.DropCollectionAsync(name);
        }

        var initializer = new MongoDbInitializer(Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
