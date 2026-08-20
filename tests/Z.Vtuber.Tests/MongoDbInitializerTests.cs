using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class MongoDbInitializerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string AuthorJson(long? pubTs) => pubTs is null
        ? """{"modules":{"module_author":{}}}"""
        : string.Format("""{{"modules":{{"module_author":{{"pub_ts":{0}}}}}}}""", pubTs);

    private static string AuthorJson(string pubTs) =>
        string.Format("""{{"modules":{{"module_author":{{"pub_ts":"{0}"}}}}}}""", pubTs);

    [Fact]
    public async Task Backfill_PopulatesPubTs_FromRawData()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson((long?)1000) });
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "2", Data = AuthorJson((long?)2000) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var record1 = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        var record2 = await collection.Find(r => r.DynamicId == "2").FirstOrDefaultAsync();
        Assert.Equal(1000, record1.PubTs);
        Assert.Equal(2000, record2.PubTs);
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson((long?)1000) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var count = await collection.CountDocumentsAsync(FilterDefinition<SpaceTimelineRecord>.Empty);
        Assert.Equal(1, count);
        var record = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        Assert.Equal(1000, record.PubTs);
    }

    [Fact]
    public async Task Backfill_LeavesMissingPubTs_Null()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson((long?)null) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var record = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        Assert.Null(record.PubTs);
    }

    [Fact]
    public async Task Backfill_PopulatesPubTs_FromStringPubTs()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson("3000") });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var record = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        Assert.Equal(3000, record.PubTs);
    }

    [Fact]
    public async Task Backfill_Marker_PreventsRerun()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var marker = new MetaRecord { Key = MongoDbInitializer.SpaceTimelinePubTsMigrationKey };
        await meta.InsertOneAsync(marker);

        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson((long?)1000) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var record = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        Assert.Null(record.PubTs);
    }

    [Fact]
    public async Task Backfill_AllUnextractable_DoesNotLoop()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new SpaceTimelineRecord { DynamicId = i.ToString(), Data = AuthorJson((long?)null) })
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var total = await collection.CountDocumentsAsync(FilterDefinition<SpaceTimelineRecord>.Empty);
        Assert.Equal(600, total);
        var withPubTs = await collection.CountDocumentsAsync(r => r.PubTs != null);
        Assert.Equal(0, withPubTs);
    }

    [Fact]
    public async Task BackfillGiftPrice_PopulatesPrice_FromGoldGiftPayload()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "gift" },
            { "Payload", """{"data":{"price":5000,"coin_type":"gold"}}""" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(5000, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_LeavesSilverGiftAtZero()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "gift" },
            { "Payload", """{"data":{"price":100,"coin_type":"silver"}}""" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_SkipsNonGiftType()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "danmaku" },
            { "Payload", """{"data":{"price":100,"coin_type":"gold"}}""" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_IsIdempotent()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "gift" },
            { "Payload", """{"data":{"price":5000,"coin_type":"gold"}}""" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var count = await collection.CountDocumentsAsync(FilterDefinition<LiveEventRecord>.Empty);
        Assert.Equal(1, count);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(5000, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_Marker_PreventsRerun()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        await meta.InsertOneAsync(new MetaRecord { Key = MongoDbInitializer.GiftPriceBackfillMigrationKey });

        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "gift" },
            { "Payload", """{"data":{"price":5000,"coin_type":"gold"}}""" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_SkipsRecordsWithExistingPrice()
    {
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "gift",
            Payload = """{"data":{"price":5000,"coin_type":"gold"}}""",
            Price = 5000,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(5000, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_HandlesInvalidPayloadGracefully()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "gift" },
            { "Payload", "invalid json" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_ProcessesLargeBatch()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new BsonDocument
            {
                { "Type", "gift" },
                { "Payload", "{\"data\":{\"price\":" + (i + 1) + ",\"coin_type\":\"gold\"}}" },
            })
            .ToList();
        await rawCollection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var count = await collection.CountDocumentsAsync(
            Builders<LiveEventRecord>.Filter.Gt(e => e.Price, 0L));
        Assert.Equal(600, count);
    }

    [Fact]
    public async Task BackfillGiftPrice_PrefersDiscountPrice_WhenBothExist()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "Type", "gift" },
            { "Payload", """{"data":{"price":200,"discount_price":150,"coin_type":"gold"}}""" },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(150, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_SilverGiftsTerminate()
    {
        var rawCollection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new BsonDocument
            {
                { "Type", "gift" },
                { "Payload", """{"data":{"price":100,"coin_type":"silver"}}""" },
            })
            .ToList();
        await rawCollection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await initializer.BackfillGiftPriceAsync(cts.Token);

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var count = await collection.CountDocumentsAsync(FilterDefinition<LiveEventRecord>.Empty);
        Assert.Equal(600, count);

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MongoDbInitializer.GiftPriceBackfillMigrationKey).AnyAsync();
        Assert.True(markerExists);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_PopulatesTextIndex_FromDanmaku()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = """{"info":["18","Hello World","123456"]}""",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(1, count);

        var record = await textIndexCollection.Find(FilterDefinition<LiveEventTextIndexRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal("Hello World", record.Text);
        Assert.Equal("danmaku", record.Type);
        Assert.Equal(123456, record.RoomId);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_PopulatesTextIndex_FromSuperChat()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "super_chat",
            Payload = """{"data":{"message":"SC Message","price":30}}""",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(1, count);

        var record = await textIndexCollection.Find(FilterDefinition<LiveEventTextIndexRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal("SC Message", record.Text);
        Assert.Equal("super_chat", record.Type);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_SkipsNonTextType()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "gift",
            Payload = """{"data":{"price":100}}""",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_SkipsRecordsWithNullText()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = """{"info":["18",null,"123456"]}""",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_IsIdempotent()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = """{"info":["18","Hello World","123456"]}""",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_Marker_PreventsRerun()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        await meta.InsertOneAsync(new MetaRecord { Key = MongoDbInitializer.LiveEventTextIndexBackfillMigrationKey });

        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = """{"info":["18","Hello World","123456"]}""",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BackfillLiveEventTextIndex_ProcessesLargeBatch()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new LiveEventRecord
            {
                Id = MongoDB.Bson.ObjectId.GenerateNewId(),
                RoomId = 123456,
                Type = "danmaku",
                Payload = $$"""{"info":["18","Message {{i}}","123456"]}""",
                OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i),
            })
            .ToList();
        await eventCollection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventTextIndexAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(600, count);
    }

    [Fact]
    public async Task EnsureIndexes_CreatesSearchIndex_OnTextIndexCollection()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var collection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        var names = await ListIndexNamesAsync(collection);
        Assert.Contains(LiveEventTextIndexRepository.SearchIndexName, names);
    }

    [Fact]
    public async Task EnsureIndexes_DropsLegacyTextIndexes()
    {
        // 模拟旧版本部署残留的索引：$text 全文索引与普通 Text 索引
        var collection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventTextIndexRecord>(
            Builders<LiveEventTextIndexRecord>.IndexKeys.Ascending(r => r.RoomId).Text(r => r.Text),
            new CreateIndexOptions { Name = "RoomId_Text_text" }));
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventTextIndexRecord>(
            Builders<LiveEventTextIndexRecord>.IndexKeys.Ascending(r => r.RoomId).Ascending(r => r.Text),
            new CreateIndexOptions { Name = "RoomId_Text_1" }));

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var names = await ListIndexNamesAsync(collection);
        Assert.DoesNotContain("RoomId_Text_text", names);
        Assert.DoesNotContain("RoomId_Text_1", names);
        Assert.Contains(LiveEventTextIndexRepository.SearchIndexName, names);
    }

    [Fact]
    public async Task EnsureIndexes_DropsLegacyLiveEventIndex()
    {
        // 模拟旧版本部署残留的索引：排序方向调整前的多 type 查询索引
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventRecord>(
            Builders<LiveEventRecord>.IndexKeys
                .Ascending(e => e.RoomId)
                .Descending(e => e.OccurredAt)
                .Ascending(e => e.Type)));

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var names = await ListIndexNamesAsync(collection);
        Assert.DoesNotContain("RoomId_1_OccurredAt_-1_Type_1", names);
        Assert.Contains("RoomId_1_Type_1_OccurredAt_1", names);
    }

    private static async Task<List<string>> ListIndexNamesAsync<T>(IMongoCollection<T> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.Select(i => i["name"].AsString).ToList();
    }
}
