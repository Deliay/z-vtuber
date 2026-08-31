using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Jobs;
using Z.Vtuber.Live;
using Z.Vtuber.Persistence;
using Z.Vtuber.Services;

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
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddHostedService<LiveGuardJob>();

        var provider = services.BuildServiceProvider();

        // 解析 IHostedService 实例以触发 LiveGuardJob 的构造（DI 依赖图完整）
        var hosted = provider.GetServices<IHostedService>().OfType<LiveGuardJob>().ToList();
        Assert.Single(hosted);
    }
}