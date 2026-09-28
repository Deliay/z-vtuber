using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class StringTolerantBsonDocumentSerializerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Read_DocumentTypePayload_ReturnsDocument()
    {
        var collection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var docPayload = new BsonDocument { { "key", "value" }, { "num", 42 } };
        await collection.InsertOneAsync(new LiveEventRecord
        {
            RoomId = 1,
            Type = "danmaku",
            Payload = docPayload,
            OccurredAt = DateTime.UtcNow
        });

        var record = await collection.Find(FilterDefinition<LiveEventRecord>.Empty).SingleAsync();

        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.Equal("value", record.Payload["key"].AsString);
        Assert.Equal(42, record.Payload["num"].AsInt32);
    }

    [Fact]
    public async Task Read_StringTypePayload_ValidJson_ReturnsParsedDocument()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);

        // Insert with string Payload directly (simulating old data)
        var rawDoc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)1L },
            { "Type", "danmaku" },
            { "Payload", """{"key":"value","num":42}""" },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
        await collection.InsertOneAsync(rawDoc);

        // Read through entity (tolerant serializer invoked)
        var entityCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var record = await entityCollection.Find(FilterDefinition<LiveEventRecord>.Empty).SingleAsync();

        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.Equal("value", record.Payload["key"].AsString);
        Assert.Equal(42, record.Payload["num"].AsInt32);
    }

    [Fact]
    public async Task Read_StringTypePayload_InvalidJson_WrapsInRaw()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);
        var rawDoc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)1L },
            { "Type", "danmaku" },
            { "Payload", "not-valid-json" },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
        await collection.InsertOneAsync(rawDoc);

        var entityCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var record = await entityCollection.Find(FilterDefinition<LiveEventRecord>.Empty).SingleAsync();

        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.True(record.Payload.Contains("raw"));
        Assert.Equal("not-valid-json", record.Payload["raw"].AsString);
    }

    [Fact]
    public async Task Read_NullPayload_ReturnsEmptyDocument()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);
        var rawDoc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)1L },
            { "Type", "danmaku" },
            { "Payload", BsonNull.Value },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
        await collection.InsertOneAsync(rawDoc);

        var entityCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var record = await entityCollection.Find(FilterDefinition<LiveEventRecord>.Empty).SingleAsync();

        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.Empty(record.Payload);
    }

    [Fact]
    public async Task Read_NonObjectJsonString_WrapsInRaw()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);
        var rawDoc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)1L },
            { "Type", "danmaku" },
            { "Payload", "[1,2,3]" },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
        await collection.InsertOneAsync(rawDoc);

        var entityCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var record = await entityCollection.Find(FilterDefinition<LiveEventRecord>.Empty).SingleAsync();

        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.True(record.Payload.Contains("raw"));
        Assert.Equal("[1,2,3]", record.Payload["raw"].AsString);
    }

    [Fact]
    public async Task Read_EmptyJsonObjectString_ReturnsEmptyDocument()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveEvent);
        var rawDoc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "RoomId", (BsonInt64)1L },
            { "Type", "danmaku" },
            { "Payload", "{}" },
            { "OccurredAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "Price", (BsonInt64)0L }
        };
        await collection.InsertOneAsync(rawDoc);

        var entityCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var record = await entityCollection.Find(FilterDefinition<LiveEventRecord>.Empty).SingleAsync();

        Assert.Equal(BsonType.Document, record.Payload.BsonType);
        Assert.Empty(record.Payload);
    }
}
