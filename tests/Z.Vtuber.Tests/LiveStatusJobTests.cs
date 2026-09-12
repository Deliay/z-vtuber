using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using MongoDB.Driver;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveStatusJobTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const long Mid = 1001;
    private const long RoomId = 123456;

    private async Task SeedAsync(long roomId)
    {
        var subscriptions = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(new SubscriptionRecord
        {
            Mid = Mid,
            Type = SubscriptionType.Danmaku,
            SubscribedAt = DateTime.UtcNow
        });

        var accounts = fixture.Database.GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accounts.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = Mid,
            RoomId = roomId,
            Data = "{}",
            FetchedAt = DateTime.UtcNow,
        });
    }

    private LiveStatusJob CreateJob(FakeBiliApiClient bili, LiveRoomInfo roomInfo)
    {
        bili.LiveRoomInfoProvider = _ => roomInfo;
        var accountInfoCrawler = new AccountInfoCrawler(
            bili,
            new AccountBasicInfoRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            NullLogger<AccountInfoCrawler>.Instance);

        return new LiveStatusJob(
            bili,
            new FakeCookieServiceClient(),
            new SubscriptionService(fixture.Database),
            accountInfoCrawler,
            new AccountBasicInfoRepository(fixture.Database),
            new LiveStatusRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            new LiveSessionTracker(),
            new BilibiliCrawlerOptions(),
            NullLogger<LiveStatusJob>.Instance);
    }

    [Fact]
    public async Task LiveToOffline_MaintainsSession()
    {
        await SeedAsync(RoomId);
        var bili = new FakeBiliApiClient();
        var job = CreateJob(bili, new LiveRoomInfo { Title = "title", RoomId = RoomId, LiveStatus = 1 });

        await job.RunOnceAsync(CancellationToken.None);

        var statusRepository = new LiveStatusRepository(fixture.Database);
        var sessionRepository = new LiveSessionRepository(fixture.Database);

        var liveStatus = await statusRepository.GetLatestByRoomIdAsync(RoomId);
        Assert.NotNull(liveStatus);
        Assert.True(liveStatus.LiveStatus);

        var (sessionTotal, sessions) = await sessionRepository.GetPageAsync(RoomId, 1, 100);
        Assert.Equal(1, sessionTotal);
        Assert.Equal(liveStatus.LiveSessionId, sessions[0].LiveId);
        Assert.False(sessions[0].IsFinish);

        // Simulate going offline in the next crawl.
        bili.LiveRoomInfoProvider = _ => new LiveRoomInfo { Title = "title", RoomId = RoomId, LiveStatus = 0 };
        await job.RunOnceAsync(CancellationToken.None);

        var closed = await sessionRepository.GetPageAsync(RoomId, 1, 100);
        Assert.Equal(1, closed.Total);
        Assert.True(closed.Items[0].IsFinish);
        Assert.NotNull(closed.Items[0].StopDate);
    }
}
