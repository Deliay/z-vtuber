using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class CookieInvalidEventTypeSplitMigrationTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private IMongoCollection<NotificationSubscriptionRecord> Collection =>
        fixture.Database.GetCollection<NotificationSubscriptionRecord>(MongoCollectionNames.NotificationSubscriptions);

    private MongoDbInitializer CreateInitializer() =>
        new(fixture.Database, NullLogger<MongoDbInitializer>.Instance);

    private async Task InsertAsync(string eventType, string groupId = "123456")
        => await Collection.InsertOneAsync(new NotificationSubscriptionRecord
        {
            EventType = eventType,
            GroupId = groupId,
            GroupName = "群",
        });

    [Fact]
    public async Task Migrate_MigratesLegacyCookieInvalid_ToBilibili()
    {
        await InsertAsync("cookie_invalid", "1");
        await InsertAsync("cookie_invalid", "2");

        await CreateInitializer().MigrateCookieInvalidEventTypeSplitAsync();

        var remaining = await Collection.Find(r => r.EventType == "cookie_invalid").ToListAsync();
        Assert.Empty(remaining);

        var bilibili = await Collection.Find(r => r.EventType == "bilibili_cookie_invalid").ToListAsync();
        Assert.Equal(2, bilibili.Count);
    }

    [Fact]
    public async Task Migrate_IsIdempotent_DoesNotDuplicate()
    {
        await InsertAsync("cookie_invalid", "1");

        var initializer = CreateInitializer();
        await initializer.MigrateCookieInvalidEventTypeSplitAsync();
        await initializer.MigrateCookieInvalidEventTypeSplitAsync();

        var count = await Collection.CountDocumentsAsync(FilterDefinition<NotificationSubscriptionRecord>.Empty);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Migrate_OtherEventTypes_Unaffected()
    {
        await InsertAsync("weibo_cookie_invalid", "1");
        await InsertAsync("bilibili_cookie_invalid", "2");
        await InsertAsync("cookie_invalid", "3");

        await CreateInitializer().MigrateCookieInvalidEventTypeSplitAsync();

        var weibo = await Collection.Find(r => r.EventType == "weibo_cookie_invalid").ToListAsync();
        Assert.Single(weibo);

        var bilibili = await Collection.Find(r => r.EventType == "bilibili_cookie_invalid").ToListAsync();
        Assert.Equal(2, bilibili.Count);
    }

    [Fact]
    public async Task Migrate_SetsMarker_AfterCompletion()
    {
        await InsertAsync("cookie_invalid", "1");

        await CreateInitializer().MigrateCookieInvalidEventTypeSplitAsync();

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MongoDbInitializer.CookieInvalidEventTypeSplitMigrationKey).AnyAsync();
        Assert.True(markerExists);
    }

    [Fact]
    public async Task Migrate_EmptyCollection_SetsMarker()
    {
        await CreateInitializer().MigrateCookieInvalidEventTypeSplitAsync();

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MongoDbInitializer.CookieInvalidEventTypeSplitMigrationKey).AnyAsync();
        Assert.True(markerExists);
    }

    [Fact]
    public async Task Migrate_ProcessesLargeBatch_CrossBatchBoundary()
    {
        var docs = Enumerable.Range(0, 600)
            .Select(i => new NotificationSubscriptionRecord
            {
                EventType = "cookie_invalid",
                GroupId = i.ToString(),
                GroupName = "群",
            })
            .ToList();
        await Collection.InsertManyAsync(docs);

        await CreateInitializer().MigrateCookieInvalidEventTypeSplitAsync();

        var remaining = await Collection.Find(r => r.EventType == "cookie_invalid").ToListAsync();
        Assert.Empty(remaining);

        var bilibili = await Collection.Find(r => r.EventType == "bilibili_cookie_invalid").ToListAsync();
        Assert.Equal(600, bilibili.Count);
    }
}
