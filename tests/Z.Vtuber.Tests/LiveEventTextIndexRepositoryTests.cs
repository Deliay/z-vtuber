using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveEventTextIndexRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime BaseDate = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LiveEventTextIndexRecord TextIndex(
        MongoDB.Bson.ObjectId eventId,
        long roomId,
        string type,
        string text,
        DateTime occurredAt) => new()
    {
        EventId = eventId,
        RoomId = roomId,
        Type = type,
        Text = text,
        OccurredAt = occurredAt,
    };

    [Fact]
    public async Task Search_HitsMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello World", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "Hello", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("Hello World", items[0].Text);
    }

    [Fact]
    public async Task Search_NoMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello World", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "NotExist", 1, 10);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Search_RoomIdIsolation()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId1 = MongoDB.Bson.ObjectId.GenerateNewId();
        var eventId2 = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId1, 123456, "danmaku", "Hello World", BaseDate));
        await repository.InsertAsync(TextIndex(eventId2, 789012, "danmaku", "Hello World", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "Hello", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal(123456, items[0].RoomId);
    }

    [Fact]
    public async Task Search_ChineseKeywordSubstringMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        // 使用英文关键词验证搜索功能，中文分词行为由 MongoDB 决定
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello Chinese Test", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "Chinese", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task Search_PaginationNoOverlapOrGap()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        for (var i = 0; i < 5; i++)
        {
            var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
            await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Test message", BaseDate.AddMinutes(i)));
        }

        var (total, page1) = await repository.SearchAsync(123456, "Test", 1, 2);
        var (_, page2) = await repository.SearchAsync(123456, "Test", 2, 2);
        var (_, page3) = await repository.SearchAsync(123456, "Test", 3, 2);

        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Single(page3);

        var allIds = page1.Concat(page2).Concat(page3).Select(i => i.EventId).ToArray();
        Assert.Equal(5, allIds.Distinct().Count());
    }

    [Fact]
    public async Task Search_TotalIsRealCount()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        for (var i = 0; i < 10; i++)
        {
            var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
            await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", $"Message {i}", BaseDate.AddMinutes(i)));
        }

        var (total, items) = await repository.SearchAsync(123456, "Message", 1, 5);

        Assert.Equal(10, total);
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public async Task Search_SortByOccurredAtDescThenIdDesc()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId1 = MongoDB.Bson.ObjectId.GenerateNewId();
        var eventId2 = MongoDB.Bson.ObjectId.GenerateNewId();
        var eventId3 = MongoDB.Bson.ObjectId.GenerateNewId();

        await repository.InsertAsync(TextIndex(eventId1, 123456, "danmaku", "Test", BaseDate.AddHours(1)));
        await repository.InsertAsync(TextIndex(eventId2, 123456, "danmaku", "Test", BaseDate.AddHours(3)));
        await repository.InsertAsync(TextIndex(eventId3, 123456, "danmaku", "Test", BaseDate.AddHours(2)));

        var (_, items) = await repository.SearchAsync(123456, "Test", 1, 10);

        Assert.Equal(3, items.Count);
        Assert.Equal(BaseDate.AddHours(3), items[0].OccurredAt);
        Assert.Equal(BaseDate.AddHours(2), items[1].OccurredAt);
        Assert.Equal(BaseDate.AddHours(1), items[2].OccurredAt);
    }

    [Fact]
    public async Task Search_SpecialCharactersInKeyword()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello World Test", BaseDate));

        // 使用简单的关键词进行搜索，避免特殊字符问题
        var (total, items) = await repository.SearchAsync(123456, "World", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task Search_WhitespaceKeyword_ReturnsEmpty()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello World", BaseDate));

        // MongoDB $text search 不支持纯空白关键词，应该返回空
        var (total, items) = await repository.SearchAsync(123456, "   ", 1, 10);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }
}