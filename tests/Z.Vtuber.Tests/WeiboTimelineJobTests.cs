using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Crawler.Weibo;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class WeiboTimelineJobTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const long Uid = 12345;

    private WeiboTimelineJob CreateJob(
        IWeiboApiClient api,
        IWeiboSubscriptionService subscription,
        WeiboTimelineRepository repository,
        FakeWeiboVisitorCookieProvider cookies,
        CrawlBackoffRegistry? backoff = null,
        WeiboCrawlerOptions? options = null) => new(
        api,
        subscription,
        repository,
        cookies,
        new CrawlLockProvider(new FakeDistributedLockProvider()),
        backoff ?? new CrawlBackoffRegistry(
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), 5),
        options ?? new WeiboCrawlerOptions { MaxPagesPerCrawl = 5 },
        NullLogger<WeiboTimelineJob>.Instance);

    private static WeiboTimelinePage Page(string mblogId, string? sinceId = null, bool hasMore = false, string? createdAt = null)
    {
        var json = @"{
            ""ok"":1,
            ""data"":{
                ""cardlistInfo"":{""since_id"":""@SINCE@"",""has_more"":@MORE@},
                ""cards"":[{""card_type"":9,""mblog"":{""id"":""@MBLOG@"",""created_at"":""@CREATED@""}}]
            }
        }"
            .Replace("@SINCE@", sinceId ?? "")
            .Replace("@MORE@", hasMore ? "true" : "false")
            .Replace("@MBLOG@", mblogId)
            .Replace("@CREATED@", createdAt ?? "Sat Mar 04 12:30:45 +0800 2023");
        return WeiboMblogParser.ParsePage(System.Text.Json.JsonDocument.Parse(json).RootElement);
    }

    private static IWeiboSubscriptionService SubscribeService(IMongoDatabase db) =>
        new WeiboSubscriptionService(new WeiboSubscriptionRepository(db));

    [Fact]
    public async Task RunOnce_CrawlsAllSubscribedUids_AndInserts()
    {
        var subscription = SubscribeService(fixture.Database);
        await subscription.SubscribeAsync(Uid);
        var api = new FakeWeiboApiClient { PageProvider = _ => Page("m1") };
        var repo = new WeiboTimelineRepository(fixture.Database);
        var job = CreateJob(api, subscription, repo, new FakeWeiboVisitorCookieProvider());

        await job.RunOnceAsync(CancellationToken.None);

        var (total, _) = await repo.GetPageAsync(Uid, 1, 100);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task RunOnce_Incremental_DoesNotDuplicateOnSecondRun()
    {
        var subscription = SubscribeService(fixture.Database);
        await subscription.SubscribeAsync(Uid);
        var api = new FakeWeiboApiClient { PageProvider = _ => Page("m1") };
        var repo = new WeiboTimelineRepository(fixture.Database);
        var job = CreateJob(api, subscription, repo, new FakeWeiboVisitorCookieProvider());

        await job.RunOnceAsync(CancellationToken.None);
        await job.RunOnceAsync(CancellationToken.None);

        var (total, items) = await repo.GetPageAsync(Uid, 1, 100);
        Assert.Equal(1, total);
        Assert.Equal("m1", items[0].MblogId);
        Assert.Equal(2, api.CalledSinceIds.Count);
    }

    [Fact]
    public async Task RunOnce_PageAllExisting_Converges_NoSecondFetch()
    {
        var subscription = SubscribeService(fixture.Database);
        await subscription.SubscribeAsync(Uid);
        var repo = new WeiboTimelineRepository(fixture.Database);
        await repo.BulkUpsertAsync([
            new() { Uid = Uid, MblogId = "m1", Data = "{}", FetchedAt = DateTime.UtcNow },
        ]);

        var api = new FakeWeiboApiClient { PageProvider = _ => Page("m1", sinceId: "s1", hasMore: true) };
        var job = CreateJob(api, subscription, repo, new FakeWeiboVisitorCookieProvider());

        await job.RunOnceAsync(CancellationToken.None);

        // 第一页 m1 已存在 → 收敛，不再翻页
        Assert.Equal([null], api.CalledSinceIds);
    }

    [Fact]
    public async Task RunOnce_RespectsMaxPagesPerCrawl()
    {
        var subscription = SubscribeService(fixture.Database);
        await subscription.SubscribeAsync(Uid);
        var options = new WeiboCrawlerOptions { MaxPagesPerCrawl = 2 };
        var api = new FakeWeiboApiClient
        {
            PageProvider = sinceId => Page($"m{sinceId}", sinceId: $"s{sinceId}", hasMore: true),
        };
        var repo = new WeiboTimelineRepository(fixture.Database);
        var job = CreateJob(api, subscription, repo, new FakeWeiboVisitorCookieProvider(), options: options);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.True(api.CalledSinceIds.Count >= 2);
        Assert.True(api.CalledSinceIds.Count <= 3);
    }

    [Fact]
    public async Task RunOnce_BackoffActive_SkipsUid()
    {
        var subscription = SubscribeService(fixture.Database);
        await subscription.SubscribeAsync(Uid);
        var backoff = new CrawlBackoffRegistry(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), 5);
        backoff.ReportRiskControl($"weibo:{Uid}");
        var api = new FakeWeiboApiClient { PageProvider = _ => Page("m1") };
        var repo = new WeiboTimelineRepository(fixture.Database);
        var job = CreateJob(api, subscription, repo, new FakeWeiboVisitorCookieProvider(), backoff: backoff);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Empty(api.CalledSinceIds);
    }

    [Fact]
    public async Task RunOnce_RiskControl_ReportsBackoff_AndRenewsCookie()
    {
        var subscription = SubscribeService(fixture.Database);
        await subscription.SubscribeAsync(Uid);
        var backoff = new CrawlBackoffRegistry(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), 5);
        var cookies = new FakeWeiboVisitorCookieProvider();
        var api = new FakeWeiboApiClient
        {
            PageProvider = _ => throw new WeiboRiskControlException("risk"),
        };
        var repo = new WeiboTimelineRepository(fixture.Database);
        var job = CreateJob(api, subscription, repo, cookies, backoff: backoff);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, cookies.RenewCount);
        Assert.False(backoff.TryAcquire($"weibo:{Uid}", out _));
    }

    [Fact]
    public async Task Subscribe_ImmediatelyTriggersCrawl()
    {
        var subscription = SubscribeService(fixture.Database);
        var api = new FakeWeiboApiClient { PageProvider = _ => Page("m1") };
        var repo = new WeiboTimelineRepository(fixture.Database);
        _ = CreateJob(api, subscription, repo, new FakeWeiboVisitorCookieProvider());

        await subscription.SubscribeAsync(Uid);

        // 轮询等待 Subscribed 事件驱动的即时补抓异步完成
        for (var i = 0; i < 50 && api.CalledSinceIds.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(api.CalledSinceIds);
    }
}
