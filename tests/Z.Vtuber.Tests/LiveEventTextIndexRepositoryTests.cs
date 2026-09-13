using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class LiveEventTextIndexRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
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
    public async Task Search_ChineseSubstringMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "今天天气不错适合出门", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "天气", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("今天天气不错适合出门", items[0].Text);
    }

    [Fact]
    public async Task Search_ChineseFullTextMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "你好世界", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "你好世界", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task Search_ChineseNoMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "今天天气不错", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "不存在", 1, 10);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Search_CaseInsensitive()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello World", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "hello", 1, 10);

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
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Price is $100.00", BaseDate));

        // Regex special characters should be escaped, not treated as regex syntax
        var (total, items) = await repository.SearchAsync(123456, "$100", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("Price is $100.00", items[0].Text);
    }

    [Fact]
    public async Task Search_RegexEscapeDotAndStar()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "file.txt", BaseDate));

        // Dots and stars should be escaped, not treated as regex wildcards
        var (total, items) = await repository.SearchAsync(123456, "file.txt", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task Search_EmptyKeyword_ReturnsEmpty()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndex(eventId, 123456, "danmaku", "Hello World", BaseDate));

        // Empty/whitespace criteria should be caught by controller validation,
        // but the repository itself should handle it gracefully
        var (total, items) = await repository.SearchAsync(123456, "   ", 1, 10);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Search_SenderNameSubstringMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello", "Zero_Ash", 403496, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "Zero", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("Zero_Ash", items[0].SenderName);
    }

    [Fact]
    public async Task Search_SenderNameCaseInsensitive()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello", "Zero_Ash", 403496, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "zero_ash", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task Search_SenderMedalMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello", "Zero_Ash", 403496, "小粉螈", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "小粉螈", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("小粉螈", items[0].SenderMedal);
    }

    [Fact]
    public async Task Search_SenderMedalCaseInsensitive()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello", "Zero_Ash", 403496, "MedalTest", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "medaltest", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task Search_UidExactMatch()
    {
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello", "Zero_Ash", 403496, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "403496", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal(403496, items[0].SenderUid);
    }

    [Fact]
    public async Task Search_UidAndTextUnion()
    {
        // 纯数字关键词同时命中 UID 和文本，取合集
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId1 = MongoDB.Bson.ObjectId.GenerateNewId();
        var eventId2 = MongoDB.Bson.ObjectId.GenerateNewId();
        // 文本含 "233" 但 UID 不是 233
        await repository.InsertAsync(TextIndexWithSender(eventId1, 123456, "danmaku", "233 test", "User1", 111, "", BaseDate));
        // UID=233 但文本不含 "233"
        await repository.InsertAsync(TextIndexWithSender(eventId2, 123456, "danmaku", "hello", "User2", 233, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "233", 1, 10);

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Search_EmptyMedalNotMatchedByMedalKeyword()
    {
        // 空 Medal 不应被粉丝牌关键词误命中
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello", "Zero_Ash", 403496, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "小粉螈", 1, 10);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Search_TextHitRegression()
    {
        // 确保文本命中行为不回退
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Hello World", "Zero_Ash", 403496, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "Hello", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("Hello World", items[0].Text);
    }

    [Fact]
    public async Task Search_Deduplication_OneRecordPerEvent()
    {
        // 一事件一记录，文本+用户名同时命中不重复
        var repository = new LiveEventTextIndexRepository(fixture.Database);
        var eventId = MongoDB.Bson.ObjectId.GenerateNewId();
        await repository.InsertAsync(TextIndexWithSender(eventId, 123456, "danmaku", "Zero_Ash test", "Zero_Ash", 403496, "", BaseDate));

        var (total, items) = await repository.SearchAsync(123456, "Zero_Ash", 1, 10);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [Fact]
    public async Task TryParseUid_PureDigits_ReturnsTrue()
    {
        var result = LiveEventTextIndexRepository.TryParseUid("123456", out var uid);

        Assert.True(result);
        Assert.Equal(123456, uid);
    }

    [Fact]
    public async Task TryParseUid_ContainsNonDigit_ReturnsFalse()
    {
        var result = LiveEventTextIndexRepository.TryParseUid("123abc", out _);

        Assert.False(result);
    }

    [Fact]
    public async Task TryParseUid_ExceedsLongRange_ReturnsFalse()
    {
        var result = LiveEventTextIndexRepository.TryParseUid("99999999999999999999999", out _);

        Assert.False(result);
    }

    [Fact]
    public async Task TryParseUid_EmptyString_ReturnsFalse()
    {
        var result = LiveEventTextIndexRepository.TryParseUid("", out _);

        Assert.False(result);
    }

    [Fact]
    public async Task TryParseUid_Whitespace_ReturnsFalse()
    {
        var result = LiveEventTextIndexRepository.TryParseUid("   ", out _);

        Assert.False(result);
    }

    [Fact]
    public async Task TryParseUid_NegativeNumber_ReturnsFalse()
    {
        var result = LiveEventTextIndexRepository.TryParseUid("-123", out _);

        Assert.False(result);
    }

    private static LiveEventTextIndexRecord TextIndexWithSender(
        MongoDB.Bson.ObjectId eventId,
        long roomId,
        string type,
        string text,
        string senderName,
        long senderUid,
        string senderMedal,
        DateTime occurredAt) => new()
    {
        EventId = eventId,
        RoomId = roomId,
        Type = type,
        Text = text,
        SenderName = senderName,
        SenderUid = senderUid,
        SenderMedal = senderMedal,
        OccurredAt = occurredAt,
    };
}
