using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Crawler.Weibo;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class StartupDiTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void LiveGuardJob_ResolvesFromServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton(new BilibiliCrawlerOptions());
        services.AddSingleton<AccountBasicInfoRepository>();
        services.AddSingleton<LiveStatusRepository>();
        services.AddSingleton<LiveEventRepository>();
        services.AddSingleton<SpaceTimelineRepository>();
        services.AddSingleton<LiveSessionRepository>();
        services.AddSingleton<LiveEventTextIndexRepository>();
        services.AddSingleton<LiveGuardRepository>();
        services.AddSingleton<ICookiePoolService, CookiePoolService>();
        services.AddSingleton<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<ILiveSessionTracker, LiveSessionTracker>();
        services.AddSingleton<IAccountInfoCrawler, AccountInfoCrawler>();
        services.AddSingleton<ICookieInvalidEventPublisher, FakeCookieInvalidEventPublisher>();
        services.AddSingleton<CookieCallGate>();
        services.AddSingleton<IBilibiliAccountProvider, BilibiliAccountProvider>();
        services.AddSingleton<BiliApiClient>();
        services.AddSingleton(sp => new BrowserishBiliApiClient(
            new HttpClientHandler(), sp.GetRequiredService<BilibiliCrawlerOptions>(),
            sp.GetRequiredService<IBilibiliAccountProvider>()));
        services.AddSingleton<IBiliApiClient>(sp => new ResilientBiliApiClient(
            sp.GetRequiredService<BiliApiClient>(),
            sp.GetRequiredService<BrowserishBiliApiClient>(),
            sp.GetRequiredService<BilibiliCrawlerOptions>(),
            sp.GetRequiredService<ICookiePoolService>(),
            sp.GetRequiredService<ICrawlBackoffRegistry>(),
            sp.GetRequiredService<ILogger<ResilientBiliApiClient>>()));
        services.AddSingleton<ICrawlBackoffRegistry, CrawlBackoffRegistry>();
        services.AddSingleton<CrawlLockProvider>();
        services.AddSingleton<LiveGuardListFetcher>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LiveGuardTop3BackfillService>();
        services.AddHostedService<LiveGuardJob>();

        var provider = services.BuildServiceProvider();

        // 解析 IHostedService 实例以触发 LiveGuardJob 的构造（DI 依赖图完整）
        var hosted = provider.GetServices<IHostedService>().OfType<LiveGuardJob>().ToList();
        Assert.Single(hosted);

        // 解析 LiveGuardTop3BackfillService 以验证其依赖可解析
        var backfillService = provider.GetRequiredService<LiveGuardTop3BackfillService>();
        Assert.NotNull(backfillService);
    }

    [Fact]
    public void WeiboTimelineJob_ResolvesFromServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton(new BilibiliCrawlerOptions());
        services.AddSingleton(new WeiboCrawlerOptions());
        services.AddSingleton(Options.Create(new BilibiliCrawlerOptions()));
        services.AddSingleton<WeiboSubscriptionRepository>();
        services.AddSingleton<WeiboTimelineRepository>();
        services.AddSingleton<IWeiboSubscriptionService, WeiboSubscriptionService>();
        services.AddSingleton<IWeiboVisitorCookieProvider, WeiboVisitorCookieProvider>();
        services.AddSingleton<IWeiboApiClient, WeiboApiClient>();
        // B 站共享退避实例（接口）
        services.AddSingleton<ICrawlBackoffRegistry>(new CrawlBackoffRegistry(Options.Create(new BilibiliCrawlerOptions())));
        // 微博链路独立的退避实例（按 WeiboCrawlerOptions 独立参数构造）
        services.AddSingleton(new CrawlBackoffRegistry(
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), 5));
        services.AddSingleton<CrawlLockProvider>();
        services.AddHostedService<WeiboTimelineJob>();

        var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().OfType<WeiboTimelineJob>().ToList();
        Assert.Single(hosted);

        // 微博退避实例必须与 B 站共享实例不同（独立参数生效）
        var weiboBackoff = provider.GetRequiredService<CrawlBackoffRegistry>();
        var bilibiliBackoff = provider.GetRequiredService<ICrawlBackoffRegistry>();
        Assert.NotSame(bilibiliBackoff, weiboBackoff);
    }
}