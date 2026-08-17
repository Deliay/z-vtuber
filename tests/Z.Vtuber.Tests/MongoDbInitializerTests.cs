using Microsoft.Extensions.Logging.Abstractions;
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
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "gift",
            Payload = """{"data":{"price":5000,"coin_type":"gold"}}""",
            Price = 0,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(5000, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_LeavesSilverGiftAtZero()
    {
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "gift",
            Payload = """{"data":{"price":100,"coin_type":"silver"}}""",
            Price = 0,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_SkipsNonGiftType()
    {
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "danmaku",
            Payload = """{"data":{"price":100,"coin_type":"gold"}}""",
            Price = 0,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_IsIdempotent()
    {
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "gift",
            Payload = """{"data":{"price":5000,"coin_type":"gold"}}""",
            Price = 0,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();
        await initializer.BackfillGiftPriceAsync();

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

        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "gift",
            Payload = """{"data":{"price":5000,"coin_type":"gold"}}""",
            Price = 0,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

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
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        await collection.InsertOneAsync(new LiveEventRecord
        {
            Type = "gift",
            Payload = "invalid json",
            Price = 0,
        });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal(0, record.Price);
    }

    [Fact]
    public async Task BackfillGiftPrice_ProcessesLargeBatch()
    {
        var collection = fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new LiveEventRecord
            {
                Type = "gift",
                Payload = """{"data":{"price":""" + (i + 1) + ""","coin_type":"gold"}}""",
                Price = 0,
            })
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillGiftPriceAsync();

        var count = await collection.CountDocumentsAsync(
            Builders<LiveEventRecord>.Filter.Gt(e => e.Price, 0L));
        Assert.Equal(600, count);
    }
}
