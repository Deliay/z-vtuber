using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class AccountInfoCrawlerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const long Mid = 1001;
    private const long RoomId = 123456;

    private static string ChannelJson(long roomId, JsonElement lives)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("code", 200);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WritePropertyName("channel");
            writer.WriteStartObject();
            writer.WriteNumber("roomId", roomId);
            writer.WriteEndObject();
            writer.WritePropertyName("lives");
            lives.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task Crawl_MergesOngoingLocalSession_WithoutDuplicate()
    {
        var localLiveId = Guid.NewGuid().ToString();
        var startDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var sessions = fixture.Database.GetCollection<LiveSessionRecord>(MongoCollectionNames.LiveSession);
        await sessions.InsertOneAsync(new LiveSessionRecord
        {
            LiveId = localLiveId,
            RoomId = RoomId,
            Title = "local-title",
            IsFinish = false,
            StartDate = startDate,
            FetchedAt = DateTime.UtcNow,
        });

        // B站 reports the same ongoing live with a different liveId but the same start time.
        using var doc = JsonDocument.Parse(ChannelJson(RoomId, JsonDocument.Parse($$"""
            [{"liveId":"bili-9","title":"bili-title","startDate":{{new DateTimeOffset(startDate).ToUnixTimeMilliseconds()}},"isFinish":false,"danmakusCount":1,"totalIncome":0,"watchCount":1,"likeCount":1,"payCount":1,"maxOnlineCount":1}]
        """).RootElement));
        var bili = new FakeBiliApiClient { AccountInfoProvider = _ => doc.RootElement.Clone() };

        var crawler = new AccountInfoCrawler(
            bili,
            new AccountBasicInfoRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            NullLogger<AccountInfoCrawler>.Instance);

        await crawler.CrawlAsync(Mid);

        var all = await new LiveSessionRepository(fixture.Database).GetPageAsync(RoomId, 1, 100);
        Assert.Equal(1, all.Total);
        Assert.Equal(localLiveId, all.Items[0].LiveId);
        Assert.Equal("bili-title", all.Items[0].Title);
    }

    [Fact]
    public async Task Crawl_HistoricalLive_UpsertsByBiliLiveId()
    {
        var startDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        using var doc = JsonDocument.Parse(ChannelJson(RoomId, JsonDocument.Parse($$"""
            [{"liveId":"bili-99","title":"hist","startDate":{{new DateTimeOffset(startDate).ToUnixTimeMilliseconds()}},"isFinish":true,"danmakusCount":2,"totalIncome":0,"watchCount":1,"likeCount":1,"payCount":1,"maxOnlineCount":1}]
        """).RootElement));
        var bili = new FakeBiliApiClient { AccountInfoProvider = _ => doc.RootElement.Clone() };

        var crawler = new AccountInfoCrawler(
            bili,
            new AccountBasicInfoRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            NullLogger<AccountInfoCrawler>.Instance);

        await crawler.CrawlAsync(Mid);

        var all = await new LiveSessionRepository(fixture.Database).GetPageAsync(RoomId, 1, 100);
        Assert.Equal(1, all.Total);
        Assert.Equal("bili-99", all.Items[0].LiveId);
    }
}
