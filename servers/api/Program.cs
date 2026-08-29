using MongoDB.Driver;
using System.Text.Json.Serialization;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Jobs;
using Z.Vtuber.Live;
using Z.Vtuber.Persistence;
using Z.Vtuber.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks();

builder.AddMongoDBClient("mongo");

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
builder.Services.AddSingleton<MongoDbInitializer>();

builder.Services.AddBackstageDataAggregation();

builder.Services.AddSingleton<ICookiePoolService, CookiePoolService>();
builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();
builder.Services.AddSingleton<ILiveSessionTracker, LiveSessionTracker>();
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
builder.Services.AddSingleton<ILiveEventBus, LiveEventBus>();
builder.Services.AddSingleton<CrawlLockProvider>();

builder.Services.AddHostedService<CookieProbeJob>();
builder.Services.AddHostedService<AccountInfoJob>();
builder.Services.AddHostedService<LiveStatusJob>();
builder.Services.AddHostedService<SpaceTimelineJob>();
builder.Services.AddHostedService<LiveEventSource>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<MongoDbInitializer>();

    // Phase 4.2: 启动顺序固定为「删旧 Mid 唯一索引 → 迁移 → 建 (Mid,Type) 复合唯一索引」
    // 1. 先删旧 Mid 唯一索引（避免迁移插入同 Mid 第二条记录时冲突）
    await initializer.DropOldSubscriptionIndexAsync();

    // 2. 执行存量迁移（在复合唯一索引建立之前，避免与唯一索引竞争）
    await initializer.MigrateSubscriptionTypeSplitAsync();

    // 3. 建 (Mid, Type) 复合唯一索引
    await initializer.EnsureSubscriptionCompositeIndexAsync();

    // 4. 其他索引与迁移
    await initializer.EnsureIndexesAsync();
    await initializer.BackfillSpaceTimelinePubTsAsync();
    await initializer.BackfillGiftPriceAsync();
    await initializer.BackfillLiveEventTextIndexAsync();
    await initializer.BackfillLiveEventSenderAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
