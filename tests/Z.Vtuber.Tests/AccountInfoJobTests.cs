using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public class AccountInfoJobTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static AccountInfoJob CreateJob(ISubscriptionService subscription, List<long> crawled)
    {
        var crawler = new RecordingAccountInfoCrawler(crawled);
        return new AccountInfoJob(
            subscription,
            crawler,
            new BilibiliCrawlerOptions(),
            NullLogger<AccountInfoJob>.Instance);
    }

    private sealed class RecordingAccountInfoCrawler(List<long> recorded) : IAccountInfoCrawler
    {
        public Task CrawlAsync(long mid, CancellationToken ct = default)
        {
            recorded.Add(mid);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Run_IncludesGuardMids_InBasicInfoCrawl()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(1, SubscriptionType.Dynamic);
        await subscription.SubscribeAsync(2, SubscriptionType.Guard);

        var crawled = new List<long>();
        var job = CreateJob(subscription, crawled);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Contains(2L, crawled);
    }

    [Fact]
    public async Task Run_SameMidAcrossThreeTypes_DedupesToSingleCrawl()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(42, SubscriptionType.Dynamic);
        await subscription.SubscribeAsync(42, SubscriptionType.Danmaku);
        await subscription.SubscribeAsync(42, SubscriptionType.Guard);

        var crawled = new List<long>();
        var job = CreateJob(subscription, crawled);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, crawled.Count(m => m == 42L));
    }
}