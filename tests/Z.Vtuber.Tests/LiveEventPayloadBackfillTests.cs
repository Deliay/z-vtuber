using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class LiveEventPayloadBackfillTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static BsonDocument CreateRawEvent(string type, string payload)
    {
        return new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)123456L },
            { "Type", type },
            { "Payload", payload },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_ConvertsStringPayloads()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        var docs = new[]
        {
            CreateRawEvent("danmaku", """{"info":["18","Hello World"]}"""),
            CreateRawEvent("gift", """{"data":{"coin_type":"gold","discount_price":100}}"""),
        };
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var records = await fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));
        Assert.True(records.First(r => r.Type == "danmaku").Payload.Contains("info"));
        Assert.True(records.First(r => r.Type == "gift").Payload.Contains("data"));
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_SkipsDirtyPayloads()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        var docs = new[]
        {
            CreateRawEvent("danmaku", "this is not json at all {broken"),
            CreateRawEvent("gift", """{"data":{"coin_type":"gold"}}"""),
        };
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var records = await fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();

        Assert.Equal(2, records.Count);

        // Dirty payload was skipped by migration (left as original string in MongoDB).
        // When read through the tolerant serializer, string is wrapped as {raw: ...}
        var dirtyRecord = records.First(r => r.Type == "danmaku");
        Assert.Equal(BsonType.Document, dirtyRecord.Payload.BsonType);
        Assert.True(dirtyRecord.Payload.Contains("raw"));
        Assert.Equal("this is not json at all {broken", dirtyRecord.Payload["raw"].AsString);

        // Valid payload should be converted to document
        var validRecord = records.First(r => r.Type == "gift");
        Assert.Equal(BsonType.Document, validRecord.Payload.BsonType);
        Assert.True(validRecord.Payload.Contains("data"));
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_IsIdempotent()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        var docs = new[] { CreateRawEvent("danmaku", """{"info":["18","Hello"]}""") };
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);

        await initializer.BackfillLiveEventPayloadObjectAsync();
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var marker = await meta.Find(r => r.Key == MongoDbInitializer.LiveEventPayloadObjectBackfillMigrationKey)
            .FirstOrDefaultAsync();
        Assert.NotNull(marker);

        var record = await fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .FirstOrDefaultAsync();
        Assert.NotNull(record);
        Assert.Equal(BsonType.Document, record.Payload.BsonType);
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_SkipsNonStringPayloads()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        var doc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)123456L },
            { "Type", "danmaku" },
            { "Payload", new BsonDocument { { "info", new BsonArray { "18", "Hello" } } } },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
        await collection.InsertOneAsync(doc);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var record = await fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .FirstOrDefaultAsync();
        Assert.NotNull(record);
        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.Equal("Hello", record.Payload["info"].AsBsonArray[1].AsString);
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_ProcessesBatch()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        var docs = Enumerable.Range(0, 600)
            .Select(i => CreateRawEvent("danmaku", $"{{\"info\":[\"18\",\"Message {i}\"]}}"))
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var records = await fixture.Database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();
        Assert.Equal(600, records.Count);
        Assert.All(records, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_AllowsConcurrentExecution()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        var docs = Enumerable.Range(0, 10)
            .Select(i => CreateRawEvent("danmaku", $"{{\"info\":[\"18\",\"Message {i}\"]}}"))
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer1 = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        var initializer2 = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);

        await Task.WhenAll(
            initializer1.BackfillLiveEventPayloadObjectAsync(),
            initializer2.BackfillLiveEventPayloadObjectAsync());

        var meta = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var marker = await meta.Find(r => r.Key == MongoDbInitializer.LiveEventPayloadObjectBackfillMigrationKey)
            .FirstOrDefaultAsync();
        Assert.NotNull(marker);
    }
}
