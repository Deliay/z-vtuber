using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

public sealed class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7.0")
        .WithEntrypoint("sh", "-c", "ulimit -n 65535 && exec docker-entrypoint.sh mongod")
        .Build();

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var client = new MongoClient(_container.GetConnectionString());
        Database = client.GetDatabase("zvtuber_test");

        await ResetAsync();
    }

    public async Task ResetAsync()
    {
        var names = new[]
        {
            MongoCollectionNames.Cookies,
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
        };

        foreach (var name in names)
        {
            await Database.DropCollectionAsync(name);
        }

        var initializer = new MongoDbInitializer(Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("mongo")]
public sealed class MongoCollection : ICollectionFixture<MongoDbFixture>;
