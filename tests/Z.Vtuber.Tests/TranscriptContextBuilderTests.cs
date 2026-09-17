using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Context;

namespace Z.Vtuber.Tests;

public class TranscriptContextBuilderTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AccountBasicInfoRepository AccountInfo => new(mongo.Database);

    private LiveGuardRepository Guards => new(mongo.Database);

    private LiveEventTextIndexRepository TextIndex => new(mongo.Database);

    private TranscriptContextBuilder Builder => new(
        AccountInfo, Guards, TextIndex, NullLogger<TranscriptContextBuilder>.Instance);

    private static RecordingRecord Recording(
        long roomId, long mid, DateTime startedAt, DateTime? endedAt) => new()
    {
        Id = ObjectId.GenerateNewId(),
        RoomId = roomId,
        Mid = mid,
        Title = "title",
        Files = [],
        StartedAt = startedAt,
        EndedAt = endedAt,
        CreatedAt = startedAt,
    };

    private Task SeedAnchorAsync(long mid, long roomId, string uName) =>
        AccountInfo.UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = roomId,
            Data = $$"""{"uName":"{{uName}}"}""",
            FetchedAt = DateTime.UtcNow,
        });

    private Task SeedGuardSnapshotAsync(
        long mid, long roomId, string date, params string[] names) =>
        Guards.UpsertDailySnapshotAsync(new LiveGuardRecord
        {
            Mid = mid,
            RoomId = roomId,
            Date = date,
            Guards = names.Select((n, i) => new LiveGuardEntry { Uid = i + 1, UserName = n }).ToList(),
            GuardCount = names.Length,
            FetchedAt = DateTime.UtcNow,
        });

    private Task SeedSuperChatAsync(
        long roomId, DateTime occurredAt, string text, string sender, string medal) =>
        TextIndex.InsertAsync(new LiveEventTextIndexRecord
        {
            Id = ObjectId.GenerateNewId(),
            EventId = ObjectId.GenerateNewId(),
            RoomId = roomId,
            Type = LiveEventTextTypes.SuperChat,
            Text = text,
            SenderName = sender,
            SenderMedal = medal,
            OccurredAt = occurredAt,
        });

    [Fact]
    public async Task Build_AllCategories_OrderedByPriority()
    {
        var now = DateTime.UtcNow;
        var recording = Recording(1001, 2001, now.AddHours(-1), now);
        await SeedAnchorAsync(2001, 1001, "主播甲");
        await SeedGuardSnapshotAsync(2001, 1001, "2026-09-16", "舰长乙", "舰长丙");
        await SeedSuperChatAsync(1001, now.AddMinutes(-30), "SC内容一", "SC用户甲", "粉丝牌A");

        var context = await Builder.BuildAsync(recording);

        Assert.Equal(new[] { "主播甲", "舰长乙", "舰长丙", "SC用户甲", "粉丝牌A", "SC内容一" }, context);
    }

    [Fact]
    public async Task Build_DeduplicatesAcrossCategories()
    {
        var now = DateTime.UtcNow;
        var recording = Recording(1002, 2002, now.AddHours(-1), now);
        await SeedGuardSnapshotAsync(2002, 1002, "2026-09-16", "同名用户");
        await SeedSuperChatAsync(1002, now.AddMinutes(-10), "hello", "同名用户", "");

        var context = await Builder.BuildAsync(recording);

        Assert.Equal(1, context.Count(c => c == "同名用户"));
    }

    [Fact]
    public async Task Build_UsesLatestGuardSnapshotOnly()
    {
        var now = DateTime.UtcNow;
        var recording = Recording(1003, 2003, now.AddHours(-1), now);
        await SeedGuardSnapshotAsync(2003, 1003, "2026-09-15", "旧舰长");
        await SeedGuardSnapshotAsync(2003, 1003, "2026-09-16", "新舰长");

        var context = await Builder.BuildAsync(recording);

        Assert.Contains("新舰长", context);
        Assert.DoesNotContain("旧舰长", context);
    }

    [Fact]
    public async Task Build_SuperChatWindow_ExcludesOutOfRangeAndOtherTypes()
    {
        var now = DateTime.UtcNow;
        var startedAt = now.AddHours(-1);
        var recording = Recording(1004, 2004, startedAt, now);
        await SeedSuperChatAsync(1004, startedAt.AddMinutes(-1), "太早", "早起用户", "");
        await SeedSuperChatAsync(1004, now.AddMinutes(1), "太晚", "晚睡用户", "");
        await SeedSuperChatAsync(1004, now.AddMinutes(-5), "窗口内", "准时用户", "");
        await TextIndex.InsertAsync(new LiveEventTextIndexRecord
        {
            Id = ObjectId.GenerateNewId(),
            EventId = ObjectId.GenerateNewId(),
            RoomId = 1004,
            Type = LiveEventTextTypes.Danmaku,
            Text = "弹幕内容",
            SenderName = "弹幕用户",
            OccurredAt = now.AddMinutes(-5),
        });

        var context = await Builder.BuildAsync(recording);

        Assert.Contains("准时用户", context);
        Assert.Contains("窗口内", context);
        Assert.DoesNotContain("早起用户", context);
        Assert.DoesNotContain("晚睡用户", context);
        Assert.DoesNotContain("弹幕内容", context);
    }

    [Fact]
    public async Task Build_MissingCategories_DegradesGracefully()
    {
        var now = DateTime.UtcNow;
        var recording = Recording(1005, 2005, now.AddHours(-1), now);
        await SeedAnchorAsync(2005, 1005, "只有主播");

        var context = await Builder.BuildAsync(recording);

        Assert.Equal(new[] { "只有主播" }, context);
    }

    [Fact]
    public async Task Build_NoData_ReturnsEmpty()
    {
        var recording = Recording(1006, 2006, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        var context = await Builder.BuildAsync(recording);

        Assert.Empty(context);
    }

    [Fact]
    public async Task Build_OverLimit_TruncatesWholeLinesAtPriorityBoundary()
    {
        var now = DateTime.UtcNow;
        var recording = Recording(1007, 2007, now.AddHours(-1), now);
        await SeedAnchorAsync(2007, 1007, "主播甲");
        var guardNames = Enumerable.Range(0, 40).Select(i => $"舰长编号{i:D2}很长很长很长").ToArray();
        await SeedGuardSnapshotAsync(2007, 1007, "2026-09-16", guardNames);

        var context = await Builder.BuildAsync(recording);

        Assert.True(string.Join("\n", context).Length <= TranscriptContextBuilder.MaxContextChars);
        Assert.True(context.Count < guardNames.Length + 1, "expected truncation");
        // 不得产生半截行：每个输出项都必须是完整的原始行
        var expected = new HashSet<string>(["主播甲", .. guardNames]);
        Assert.All(context, line => Assert.Contains(line, expected));
    }

    [Fact]
    public void Truncate_StopsBeforeExceedingLimit()
    {
        var ordered = new[] { "aaa", "bbb", "ccc" };

        var result = TranscriptContextBuilder.Truncate(ordered, 7);

        Assert.Equal(new[] { "aaa", "bbb" }, result);
    }

    [Theory]
    [InlineData("""{"uName":"名字"}""", "名字")]
    [InlineData("""{"other":1}""", null)]
    [InlineData("not-json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractUName_DefensiveParsing(string? data, string? expected)
    {
        Assert.Equal(expected, TranscriptContextBuilder.ExtractUName(data));
    }
}
