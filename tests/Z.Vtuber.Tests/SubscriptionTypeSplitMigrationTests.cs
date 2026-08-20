using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class SubscriptionTypeSplitMigrationTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// 插入无 Type 字段的存量文档（模拟旧版本数据）
    /// </summary>
    private async Task InsertLegacyRecordAsync(long mid, DateTime subscribedAt)
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.Subscriptions);
        var doc = new BsonDocument
        {
            { "Mid", mid },
            { "SubscribedAt", subscribedAt }
        };
        await collection.InsertOneAsync(doc);
    }

    [Fact]
    public async Task Migrate_SplitsLegacyRecords_IntoDualTypes()
    {
        var subscribedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await InsertLegacyRecordAsync(1001, subscribedAt);
        await InsertLegacyRecordAsync(1002, subscribedAt);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var collection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        var count = await collection.CountDocumentsAsync(FilterDefinition<SubscriptionRecord>.Empty);
        Assert.Equal(4, count);

        var dynamic1001 = await collection.Find(s => s.Mid == 1001 && s.Type == SubscriptionType.Dynamic).FirstOrDefaultAsync();
        var danmaku1001 = await collection.Find(s => s.Mid == 1001 && s.Type == SubscriptionType.Danmaku).FirstOrDefaultAsync();
        Assert.NotNull(dynamic1001);
        Assert.NotNull(danmaku1001);
        Assert.Equal(subscribedAt, dynamic1001.SubscribedAt);
        Assert.Equal(subscribedAt, danmaku1001.SubscribedAt);

        var dynamic1002 = await collection.Find(s => s.Mid == 1002 && s.Type == SubscriptionType.Dynamic).FirstOrDefaultAsync();
        var danmaku1002 = await collection.Find(s => s.Mid == 1002 && s.Type == SubscriptionType.Danmaku).FirstOrDefaultAsync();
        Assert.NotNull(dynamic1002);
        Assert.NotNull(danmaku1002);
    }

    [Fact]
    public async Task Migrate_IsIdempotent_DoesNotDuplicateRecords()
    {
        var subscribedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await InsertLegacyRecordAsync(1001, subscribedAt);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var collection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        var count = await collection.CountDocumentsAsync(FilterDefinition<SubscriptionRecord>.Empty);
        Assert.Equal(2, count);

        var dynamic1001 = await collection.Find(s => s.Mid == 1001 && s.Type == SubscriptionType.Dynamic).FirstOrDefaultAsync();
        var danmaku1001 = await collection.Find(s => s.Mid == 1001 && s.Type == SubscriptionType.Danmaku).FirstOrDefaultAsync();
        Assert.NotNull(dynamic1001);
        Assert.NotNull(danmaku1001);
    }

    [Fact]
    public async Task Migrate_AlreadyTypedRecords_AreNotAffected()
    {
        var subscribedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 插入一条已有 Type 的记录（使用强类型对象）
        var collection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await collection.InsertOneAsync(new SubscriptionRecord
        {
            Mid = 1001,
            Type = SubscriptionType.Dynamic,
            SubscribedAt = subscribedAt
        });

        // 插入一条无 Type 的存量记录
        await InsertLegacyRecordAsync(1002, subscribedAt);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var count = await collection.CountDocumentsAsync(FilterDefinition<SubscriptionRecord>.Empty);
        Assert.Equal(3, count); // 1001 Dynamic + 1002 Dynamic + 1002 Danmaku

        var dynamic1001 = await collection.Find(s => s.Mid == 1001 && s.Type == SubscriptionType.Dynamic).FirstOrDefaultAsync();
        Assert.NotNull(dynamic1001);
        var danmaku1001 = await collection.Find(s => s.Mid == 1001 && s.Type == SubscriptionType.Danmaku).FirstOrDefaultAsync();
        Assert.Null(danmaku1001); // 已有 Type 的记录不会被拆分
    }

    [Fact]
    public async Task Migrate_SetsMarker_AfterCompletion()
    {
        await InsertLegacyRecordAsync(1001, DateTime.UtcNow);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MongoDbInitializer.SubscriptionTypeSplitMigrationKey).AnyAsync();
        Assert.True(markerExists);
    }

    [Fact]
    public async Task Migrate_Marker_PreventsRerun()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        await meta.InsertOneAsync(new MetaRecord { Key = MongoDbInitializer.SubscriptionTypeSplitMigrationKey });

        await InsertLegacyRecordAsync(1001, DateTime.UtcNow);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.Subscriptions);
        var count = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.Equal(1, count); // 未迁移，因为 marker 已存在
    }

    [Fact]
    public async Task Migrate_ProcessesLargeBatch_CrossBatchBoundary()
    {
        var subscribedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 插入 600 条存量记录（跨两个 batch，每个 batch 500）
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.Subscriptions);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new BsonDocument
            {
                { "Mid", 1000 + i },
                { "SubscribedAt", subscribedAt }
            })
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var subscriptionCollection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        var count = await subscriptionCollection.CountDocumentsAsync(FilterDefinition<SubscriptionRecord>.Empty);
        Assert.Equal(1200, count); // 600 * 2

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MongoDbInitializer.SubscriptionTypeSplitMigrationKey).AnyAsync();
        Assert.True(markerExists);
    }

    [Fact]
    public async Task Migrate_EmptyCollection_DoesNothing()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.MigrateSubscriptionTypeSplitAsync();

        var collection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        var count = await collection.CountDocumentsAsync(FilterDefinition<SubscriptionRecord>.Empty);
        Assert.Equal(0, count);

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MongoDbInitializer.SubscriptionTypeSplitMigrationKey).AnyAsync();
        Assert.True(markerExists);
    }

    [Fact]
    public async Task DropOldSubscriptionIndex_DropsIfExisting()
    {
        var collection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);

        // 创建旧的 Mid_1 唯一索引
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<SubscriptionRecord>(
            Builders<SubscriptionRecord>.IndexKeys.Ascending(s => s.Mid),
            new CreateIndexOptions { Unique = true, Name = "Mid_1" }));

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.DropOldSubscriptionIndexAsync();

        var names = await ListIndexNamesAsync(collection);
        Assert.DoesNotContain("Mid_1", names);
    }

    [Fact]
    public async Task EnsureSubscriptionCompositeIndex_CreatesIndex()
    {
        var collection = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureSubscriptionCompositeIndexAsync();

        var names = await ListIndexNamesAsync(collection);
        Assert.Contains("Mid_1_Type_1", names);
    }

    private static async Task<List<string>> ListIndexNamesAsync<T>(IMongoCollection<T> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.Select(i => i["name"].AsString).ToList();
    }
}
