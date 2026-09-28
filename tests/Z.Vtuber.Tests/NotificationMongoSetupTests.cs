using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Notification 域索引初始化与 cookie_invalid 类型拆分迁移。</summary>
public class NotificationMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private NotificationMongoSetup Setup() => new(fixture.Database, NullLogger<NotificationMongoSetup>.Instance);

    [Fact]
    public async Task EnsureIndexes_CreatesUniqueCompositeIndex()
    {
        await Setup().EnsureIndexesAsync();

        var names = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<NotificationSubscriptionRecord>(NotificationCollectionNames.NotificationSubscriptions));
        Assert.Contains("EventType_1_GroupId_1", names);
    }

    [Fact]
    public async Task MigrateAsync_ConvertsLegacyCookieInvalid_AndWritesMarker()
    {
        var collection = fixture.Database.GetCollection<NotificationSubscriptionRecord>(NotificationCollectionNames.NotificationSubscriptions);
        await collection.InsertOneAsync(new NotificationSubscriptionRecord { EventType = "cookie_invalid", GroupId = "1", GroupName = "群" });

        var setup = Setup();
        await setup.MigrateAsync();

        Assert.True(await collection.Find(n => n.EventType == "bilibili_cookie_invalid").AnyAsync());
        Assert.False(await collection.Find(n => n.EventType == "cookie_invalid").AnyAsync());

        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);
        Assert.True(await meta.Find(r => r.Key == NotificationMongoSetup.CookieInvalidEventTypeSplitMigrationKey).AnyAsync());
    }
}
