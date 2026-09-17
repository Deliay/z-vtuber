using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class MongoDbInitializerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
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
    public async Task Backfill_ConcurrentRuns_DoNotThrowDuplicateKey()
    {
        // api 与 crawler 双服务并发启动初始化，都会写同一个完成 marker。
        // 回归：BackfillSpaceTimelinePubTsAsync 的 marker 写入必须为 upsert，
        // 否则两个服务同时在 marker 不存在时 InsertOne 同一 key 会抛 DuplicateKey。
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson((long?)1000) });

        var initializerA = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        var initializerB = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);

        await Task.WhenAll(
            initializerA.BackfillSpaceTimelinePubTsAsync(),
            initializerB.BackfillSpaceTimelinePubTsAsync());

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerCount = await meta.CountDocumentsAsync(r => r.Key == MongoDbInitializer.SpaceTimelinePubTsMigrationKey);
        Assert.Equal(1, markerCount);

        var recordCount = await collection.CountDocumentsAsync(FilterDefinition<SpaceTimelineRecord>.Empty);
        Assert.Equal(1, recordCount);
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"data":{"price":5000,"coin_type":"gold"}}"""),
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World","123456"]}"""),
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"data":{"message":"SC Message","price":30}}"""),
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"data":{"price":100}}"""),
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18",null,"123456"]}"""),
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World","123456"]}"""),
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
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World","123456"]}"""),
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
                Payload = MongoDB.Bson.BsonDocument.Parse($"{{\"info\":[\"18\",\"Message {i}\",\"123456\"]}}"),
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
        // 模拟 72cab58 误将排序回退为升序时创建的升序索引（降序为 6e23b69 显式需求）
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventRecord>(
            Builders<LiveEventRecord>.IndexKeys
                .Ascending(e => e.RoomId)
                .Ascending(e => e.Type)
                .Ascending(e => e.OccurredAt),
            new CreateIndexOptions { Name = "RoomId_1_Type_1_OccurredAt_1" }));

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var names = await ListIndexNamesAsync(collection);
        Assert.DoesNotContain("RoomId_1_Type_1_OccurredAt_1", names);
        Assert.Contains("RoomId_1_OccurredAt_-1_Type_1", names);
    }

    [Fact]
    public async Task EnsureIndexes_CreatesWeiboSubscriptionUniqueIndex()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var collection = fixture.Database.GetCollection<WeiboSubscriptionRecord>(MongoCollectionNames.WeiboSubscriptions);
        var names = await ListIndexNamesAsync(collection);
        Assert.Contains("Uid_1", names);
    }

    [Fact]
    public async Task EnsureIndexes_CreatesWeiboTimelineUniqueIndex()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var collection = fixture.Database.GetCollection<WeiboTimelineRecord>(MongoCollectionNames.WeiboUserTimeline);
        var names = await ListIndexNamesAsync(collection);
        Assert.Contains("MblogId_1", names);
        Assert.Contains("Uid_1_PubTs_-1", names);
        Assert.Contains("Uid_1_MblogId_-1", names);
    }

    [Fact]
    public async Task EnsureIndexes_CreatesNotificationSubscriptionUniqueIndex()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var collection = fixture.Database.GetCollection<NotificationSubscriptionRecord>(MongoCollectionNames.NotificationSubscriptions);
        var names = await ListIndexNamesAsync(collection);
        Assert.Contains("EventType_1_GroupId_1", names);
    }

    [Fact]
    public async Task EnsureIndexes_IsIdempotent_ForNotificationSubscriptions()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();
        await initializer.EnsureIndexesAsync();

        var collection = fixture.Database.GetCollection<NotificationSubscriptionRecord>(MongoCollectionNames.NotificationSubscriptions);
        var names = await ListIndexNamesAsync(collection);
        Assert.Single(names, n => n == "EventType_1_GroupId_1");
    }

    [Fact]
    public async Task EnsureIndexes_IsIdempotent_ForWeiboCollections()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();
        await initializer.EnsureIndexesAsync();

        var subscriptionCollection = fixture.Database.GetCollection<WeiboSubscriptionRecord>(MongoCollectionNames.WeiboSubscriptions);
        var names = await ListIndexNamesAsync(subscriptionCollection);
        Assert.Single(names, n => n == "Uid_1");
    }

    [Fact]
    public async Task EnsureIndexes_CreatesWeiboCookieIndexes()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();

        var cookies = fixture.Database.GetCollection<CookieRecord>(MongoCollectionNames.WeiboCookies);
        var cookieNames = await ListIndexNamesAsync(cookies);
        Assert.Contains("Status_1_LastUsedAt_1", cookieNames);
        Assert.Contains("AccountId_1", cookieNames);

        var sessions = fixture.Database.GetCollection<CookieSessionRecord>(MongoCollectionNames.WeiboCookieSessions);
        var sessionNames = await ListIndexNamesAsync(sessions);
        Assert.Contains("Status_1", sessionNames);
        Assert.Contains("CookieId_1", sessionNames);
    }

    [Fact]
    public async Task EnsureIndexes_IsIdempotent_ForWeiboCookieCollections()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();
        await initializer.EnsureIndexesAsync();

        var cookies = fixture.Database.GetCollection<CookieRecord>(MongoCollectionNames.WeiboCookies);
        var cookieNames = await ListIndexNamesAsync(cookies);
        Assert.Single(cookieNames, n => n == "Status_1_LastUsedAt_1");
        Assert.Single(cookieNames, n => n == "AccountId_1");

        var sessions = fixture.Database.GetCollection<CookieSessionRecord>(MongoCollectionNames.WeiboCookieSessions);
        var sessionNames = await ListIndexNamesAsync(sessions);
        Assert.Single(sessionNames, n => n == "Status_1");
        Assert.Single(sessionNames, n => n == "CookieId_1");
    }

    [Fact]
    public async Task CleanupTranscriptModelIsDefault_UnsetsField_AndPreservesOtherFields()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels);
        var legacyTrueId = ObjectId.GenerateNewId();
        var legacyFalseId = ObjectId.GenerateNewId();
        var untouchedId = ObjectId.GenerateNewId();
        await collection.InsertManyAsync(
        [
            new BsonDocument
            {
                { "_id", legacyTrueId },
                { "Name", "model-a" },
                { "Provider", "ali-fun-asr" },
                { "Type", "streaming" },
                { "ApiKey", "key-a" },
                { "Model", "fun-asr-realtime" },
                { "Enabled", true },
                { "MaxConcurrency", 2 },
                { "IsDefault", true },
            },
            new BsonDocument
            {
                { "_id", legacyFalseId },
                { "Name", "model-b" },
                { "Model", "paraformer-realtime-v2" },
                { "Enabled", false },
                { "IsDefault", false },
            },
            new BsonDocument
            {
                { "_id", untouchedId },
                { "Name", "model-c" },
                { "Model", "fun-asr-realtime" },
                { "Enabled", true },
            },
        ]);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.CleanupTranscriptModelIsDefaultAsync();

        var legacyTrue = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", legacyTrueId)).FirstAsync();
        Assert.False(legacyTrue.Contains("IsDefault"));
        Assert.Equal("model-a", legacyTrue["Name"].AsString);
        Assert.Equal("key-a", legacyTrue["ApiKey"].AsString);
        Assert.Equal("fun-asr-realtime", legacyTrue["Model"].AsString);
        Assert.True(legacyTrue["Enabled"].AsBoolean);
        Assert.Equal(2, legacyTrue["MaxConcurrency"].AsInt32);

        var legacyFalse = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", legacyFalseId)).FirstAsync();
        Assert.False(legacyFalse.Contains("IsDefault"));
        Assert.Equal("model-b", legacyFalse["Model"].AsString);

        var untouched = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", untouchedId)).FirstAsync();
        Assert.Equal("model-c", untouched["Name"].AsString);
        Assert.Equal(3, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task CleanupTranscriptModelIsDefault_IsIdempotent_AndWritesMarkerOnce()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels);
        var id = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "Name", "model-a" },
            { "IsDefault", true },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.CleanupTranscriptModelIsDefaultAsync();
        await initializer.CleanupTranscriptModelIsDefaultAsync();

        var record = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();
        Assert.False(record.Contains("IsDefault"));
        Assert.Equal(1, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        Assert.Equal(1, await meta.CountDocumentsAsync(
            r => r.Key == MongoDbInitializer.TranscriptModelIsDefaultCleanupMigrationKey));
    }

    [Fact]
    public async Task CleanupTranscriptModelIsDefault_Marker_PreventsRerun()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        await meta.InsertOneAsync(new MetaRecord
        {
            Key = MongoDbInitializer.TranscriptModelIsDefaultCleanupMigrationKey,
        });

        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels);
        var id = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "Name", "model-a" },
            { "IsDefault", true },
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.CleanupTranscriptModelIsDefaultAsync();

        var record = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();
        Assert.True(record.Contains("IsDefault"));
    }

    [Fact]
    public async Task CleanupTranscriptModelIsDefault_EmptyCollection_MarksApplied()
    {
        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.CleanupTranscriptModelIsDefaultAsync();

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        Assert.True(await meta.Find(
            r => r.Key == MongoDbInitializer.TranscriptModelIsDefaultCleanupMigrationKey).AnyAsync());
    }

    [Fact]
    public async Task CleanupTranscriptModelIsDefault_ConcurrentRuns_DoNotThrowDuplicateKey()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptModels);
        var id = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "Name", "model-a" },
            { "IsDefault", true },
        });

        var initializerA = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        var initializerB = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);

        await Task.WhenAll(
            initializerA.CleanupTranscriptModelIsDefaultAsync(),
            initializerB.CleanupTranscriptModelIsDefaultAsync());

        var record = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();
        Assert.False(record.Contains("IsDefault"));

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        Assert.Equal(1, await meta.CountDocumentsAsync(
            r => r.Key == MongoDbInitializer.TranscriptModelIsDefaultCleanupMigrationKey));
    }

    private static async Task<List<string>> ListIndexNamesAsync<T>(IMongoCollection<T> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.Select(i => i["name"].AsString).ToList();
    }
}
