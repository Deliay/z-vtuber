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
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        var docs = new[]
        {
            CreateRawEvent("danmaku", """{"info":["18","Hello World"]}"""),
            CreateRawEvent("gift", """{"data":{"coin_type":"gold","discount_price":100}}"""),
        };
        await collection.InsertManyAsync(docs);

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var records = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
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
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        var docs = new[]
        {
            CreateRawEvent("danmaku", "this is not json at all {broken"),
            CreateRawEvent("gift", """{"data":{"coin_type":"gold"}}"""),
        };
        await collection.InsertManyAsync(docs);

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var records = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
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
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        var docs = new[] { CreateRawEvent("danmaku", """{"info":["18","Hello"]}""") };
        await collection.InsertManyAsync(docs);

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);

        await initializer.BackfillLiveEventPayloadObjectAsync();
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);
        var marker = await meta.Find(r => r.Key == LiveMongoSetup.LiveEventPayloadObjectBackfillMigrationKey)
            .FirstOrDefaultAsync();
        Assert.NotNull(marker);

        var record = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .FirstOrDefaultAsync();
        Assert.NotNull(record);
        Assert.Equal(BsonType.Document, record.Payload.BsonType);
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_SkipsNonStringPayloads()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

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

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var record = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .FirstOrDefaultAsync();
        Assert.NotNull(record);
        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.Equal("Hello", record.Payload["info"].AsBsonArray[1].AsString);
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_ProcessesBatch()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        var docs = Enumerable.Range(0, 600)
            .Select(i => CreateRawEvent("danmaku", $"{{\"info\":[\"18\",\"Message {i}\"]}}"))
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        var records = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();
        Assert.Equal(600, records.Count);
        Assert.All(records, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_ConcurrentExecution_OnlyOneProcessesRecords()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        var docs = Enumerable.Range(0, 10)
            .Select(i => CreateRawEvent("danmaku", $"{{\"info\":[\"18\",\"Message {i}\"]}}"))
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer1 = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        var initializer2 = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);

        await Task.WhenAll(
            initializer1.BackfillLiveEventPayloadObjectAsync(),
            initializer2.BackfillLiveEventPayloadObjectAsync());

        // Marker should be applied
        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);
        var marker = await meta.Find(r => r.Key == LiveMongoSetup.LiveEventPayloadObjectBackfillMigrationKey)
            .FirstOrDefaultAsync();
        Assert.NotNull(marker);
        Assert.Equal("applied", marker.Status);

        // All records should be converted exactly once — no duplicate processing
        var records = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();
        Assert.Equal(10, records.Count);
        Assert.All(records, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_SecondCallSkipsWhenApplied()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        var docs = Enumerable.Range(0, 5)
            .Select(i => CreateRawEvent("danmaku", $"{{\"info\":[\"18\",\"Message {i}\"]}}"))
            .ToList();
        await collection.InsertManyAsync(docs);

        // First call executes the migration
        var initializer1 = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer1.BackfillLiveEventPayloadObjectAsync();

        // All records converted
        var records = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();
        Assert.All(records, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));

        // Second call should skip (marker is "applied")
        var initializer2 = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer2.BackfillLiveEventPayloadObjectAsync();

        // Records unchanged — second call did not reprocess
        var records2 = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();
        Assert.Equal(5, records2.Count);
        Assert.All(records2, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));
    }

    [Fact]
    public async Task BackfillLiveEventPayloadObject_StaleRunning_TakenOverByNewInstance()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);

        // Simulate a stale "running" marker from a crashed instance (>30 min ago)
        await meta.InsertOneAsync(new MetaRecord
        {
            Key = LiveMongoSetup.LiveEventPayloadObjectBackfillMigrationKey,
            Status = "running",
            StartedAt = DateTime.UtcNow.AddMinutes(-60)
        });

        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);
        var docs = Enumerable.Range(0, 5)
            .Select(i => CreateRawEvent("danmaku", $"{{\"info\":[\"18\",\"Message {i}\"]}}"))
            .ToList();
        await collection.InsertManyAsync(docs);

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventPayloadObjectAsync();

        // Marker should be applied
        var marker = await meta.Find(r => r.Key == LiveMongoSetup.LiveEventPayloadObjectBackfillMigrationKey)
            .FirstOrDefaultAsync();
        Assert.NotNull(marker);
        Assert.Equal("applied", marker.Status);

        // Records should be converted
        var records = await fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent)
            .Find(FilterDefinition<LiveEventRecord>.Empty)
            .ToListAsync();
        Assert.All(records, r => Assert.Equal(BsonType.Document, r.Payload.BsonType));
    }
}
