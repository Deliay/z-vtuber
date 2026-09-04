using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Crawler.Live;
using Z.Vtuber.Crawler.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<Z.Vtuber.Crawler.MongoDbHealthCheck>("mongo");

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

builder.Services.AddOptions<BilibiliCrawlerOptions>()
    .Bind(builder.Configuration.GetSection(BilibiliCrawlerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BilibiliCrawlerOptions>>().Value);

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<AccountBasicInfoRepository>();
builder.Services.AddSingleton<LiveStatusRepository>();
builder.Services.AddSingleton<LiveEventRepository>();
builder.Services.AddSingleton<SpaceTimelineRepository>();
builder.Services.AddSingleton<LiveSessionRepository>();
builder.Services.AddSingleton<LiveEventTextIndexRepository>();
builder.Services.AddSingleton<LiveGuardRepository>();
builder.Services.AddSingleton<MongoDbInitializer>();

builder.Services.AddBackstageDataAggregation();

builder.Services.AddSingleton<ICookiePoolService, CookiePoolService>();
builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();
builder.Services.AddSingleton<ILiveSessionTracker, LiveSessionTracker>();

builder.Services.AddSingleton<ICookieNavProbeService, CookieNavProbeService>();
builder.Services.AddSingleton<IAccountInfoCrawler, AccountInfoCrawler>();

builder.Services.AddSingleton<IBilibiliAccountProvider, BilibiliAccountProvider>();
builder.Services.AddSingleton<BiliApiClient>();
// 拟真通道：HttpMessageHandler 不可由容器自动解析，工厂注册并挂入 RiskControlHandler
builder.Services.AddSingleton(sp => new BrowserishBiliApiClient(
    new RiskControlHandler(new HttpClientHandler()),
    sp.GetRequiredService<BilibiliCrawlerOptions>(),
    sp.GetRequiredService<IBilibiliAccountProvider>()));
// IBiliApiClient 解析为装饰器：直连/拟真以具体类型注入，避免自引用循环依赖
builder.Services.AddSingleton<IBiliApiClient>(sp => new ResilientBiliApiClient(
    sp.GetRequiredService<BiliApiClient>(),
    sp.GetRequiredService<BrowserishBiliApiClient>(),
    sp.GetRequiredService<BilibiliCrawlerOptions>(),
    sp.GetRequiredService<ICookiePoolService>(),
    sp.GetRequiredService<ICrawlBackoffRegistry>(),
    sp.GetRequiredService<ILogger<ResilientBiliApiClient>>()));
builder.Services.AddSingleton<ICrawlBackoffRegistry, CrawlBackoffRegistry>();
builder.Services.AddSingleton<DanmakuClientFactory>();
builder.Services.AddSingleton<ILiveEventPublisher, RedisLiveEventPublisher>();
builder.Services.AddSingleton<CrawlLockProvider>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LiveGuardListFetcher>();
builder.Services.AddSingleton<LiveGuardTop3BackfillService>();

builder.Services.AddHostedService<CookieProbeJob>();
builder.Services.AddHostedService<AccountInfoJob>();
builder.Services.AddHostedService<LiveStatusJob>();
builder.Services.AddHostedService<SpaceTimelineJob>();
builder.Services.AddHostedService<LiveGuardJob>();
builder.Services.AddHostedService<LiveEventSource>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<MongoDbInitializer>();

    // 幂等的索引初始化与回填，与 api 服务并存执行安全
    await initializer.DropOldSubscriptionIndexAsync();
    await initializer.MigrateSubscriptionTypeSplitAsync();
    await initializer.EnsureSubscriptionCompositeIndexAsync();
    await initializer.EnsureIndexesAsync();
    await initializer.BackfillSpaceTimelinePubTsAsync();
    await initializer.BackfillGiftPriceAsync();
    await initializer.BackfillLiveEventTextIndexAsync();
    await initializer.BackfillLiveEventSenderAsync();
}

app.MapHealthChecks("/health");

_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var backfillService = scope.ServiceProvider.GetRequiredService<LiveGuardTop3BackfillService>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        await backfillService.ExecuteAsync(lifetime.ApplicationStopping);
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Top3 backfill failed.");
    }
});

app.Run();
