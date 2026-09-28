using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class LiveEventSenderBackfillTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BackfillLiveEventSender_PopulatesSenderFields_FromDanmaku()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World",[403496,"Zero_Ash",123],[25,"小粉螈","枝堇Sumire"]]}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var record = await textIndexCollection.Find(FilterDefinition<LiveEventTextIndexRecord>.Empty).FirstOrDefaultAsync();

        Assert.NotNull(record);
        Assert.Equal("Hello World", record.Text);
        Assert.Equal("Zero_Ash", record.SenderName);
        Assert.Equal(403496, record.SenderUid);
        Assert.Equal("小粉螈", record.SenderMedal);
    }

    [Fact]
    public async Task BackfillLiveEventSender_PopulatesSenderFields_FromSuperChat()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "super_chat",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"data":{"message":"SC Message","uid":663620883,"user_info":{"uname":"lxl-lxl"},"medal_info":{"medal_name":"小粉螈"}}}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var record = await textIndexCollection.Find(FilterDefinition<LiveEventTextIndexRecord>.Empty).FirstOrDefaultAsync();

        Assert.NotNull(record);
        Assert.Equal("SC Message", record.Text);
        Assert.Equal("lxl-lxl", record.SenderName);
        Assert.Equal(663620883, record.SenderUid);
        Assert.Equal("小粉螈", record.SenderMedal);
    }

    [Fact]
    public async Task BackfillLiveEventSender_SkipsNonTextType()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "gift",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"data":{"price":100}}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BackfillLiveEventSender_SkipsRecordsWithNullText()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18",null,"123456"]}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BackfillLiveEventSender_IsIdempotent()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World",[403496,"Zero_Ash"]]}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(1, count);

        var record = await textIndexCollection.Find(FilterDefinition<LiveEventTextIndexRecord>.Empty).FirstOrDefaultAsync();
        Assert.Equal("Zero_Ash", record.SenderName);
        Assert.Equal(403496, record.SenderUid);
    }

    [Fact]
    public async Task BackfillLiveEventSender_Marker_PreventsRerun()
    {
        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);
        await meta.InsertOneAsync(new MetaRecord { Key = LiveMongoSetup.LiveEventSenderBackfillMigrationKey });

        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World",[403496,"Zero_Ash"]]}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BackfillLiveEventSender_PopulatesSenderFields_WithMissingSenderInfo()
    {
        // 弹幕中 info[2] 缺失时发送者字段降级为空值
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World"]}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var record = await textIndexCollection.Find(FilterDefinition<LiveEventTextIndexRecord>.Empty).FirstOrDefaultAsync();

        Assert.NotNull(record);
        Assert.Equal("Hello World", record.Text);
        Assert.Equal("", record.SenderName);
        Assert.Equal(0, record.SenderUid);
        Assert.Equal("", record.SenderMedal);
    }

    [Fact]
    public async Task BackfillLiveEventSender_ProcessesLargeBatch()
    {
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var docs = Enumerable.Range(0, 600)
            .Select(i => new LiveEventRecord
            {
                Id = MongoDB.Bson.ObjectId.GenerateNewId(),
                RoomId = 123456,
                Type = "danmaku",
                Payload = MongoDB.Bson.BsonDocument.Parse($"{{\"info\":[\"18\",\"Message {i}\",[403496,\"Zero_Ash\"]]}}"),
                OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i),
            })
            .ToList();
        await eventCollection.InsertManyAsync(docs);

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var count = await textIndexCollection.CountDocumentsAsync(FilterDefinition<LiveEventTextIndexRecord>.Empty);
        Assert.Equal(600, count);

        var withSenderName = await textIndexCollection.CountDocumentsAsync(r => r.SenderName == "Zero_Ash");
        Assert.Equal(600, withSenderName);
    }

    [Fact]
    public async Task BackfillLiveEventSender_UpdatesExistingRecord_WithSenderFields()
    {
        // 存量记录（v1 回填）缺少发送者字段，回填后应补齐
        var textIndexCollection = fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);
        var eventCollection = fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();

        // 先插入一条只有 Text 的存量事件
        await eventCollection.InsertOneAsync(new LiveEventRecord
        {
            Id = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Payload = MongoDB.Bson.BsonDocument.Parse("""{"info":["18","Hello World",[403496,"Zero_Ash"]]}"""),
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        // 手动创建一条只有 Text 的索引记录（模拟 v1 回填结果）
        await textIndexCollection.InsertOneAsync(new LiveEventTextIndexRecord
        {
            EventId = eventId,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        });

        var initializer = new LiveMongoSetup(fixture.Database, NullLogger<LiveMongoSetup>.Instance);
        await initializer.BackfillLiveEventSenderAsync();

        var record = await textIndexCollection.Find(r => r.EventId == eventId).FirstOrDefaultAsync();
        Assert.NotNull(record);
        Assert.Equal("Hello World", record.Text);
        Assert.Equal("Zero_Ash", record.SenderName);
        Assert.Equal(403496, record.SenderUid);
    }
}
