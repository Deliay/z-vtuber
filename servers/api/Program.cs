using MongoDB.Driver;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Jobs;
using Z.Vtuber.Live;
using Z.Vtuber.Persistence;
using Z.Vtuber.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.AddMongoDBClient("mongo");

var biliOptions = builder.Configuration
    .GetSection(BilibiliCrawlerOptions.SectionName)
    .Get<BilibiliCrawlerOptions>() ?? new BilibiliCrawlerOptions();
builder.Services.AddSingleton(biliOptions);

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<AccountBasicInfoRepository>();
builder.Services.AddSingleton<LiveStatusRepository>();
builder.Services.AddSingleton<LiveEventRepository>();
builder.Services.AddSingleton<SpaceTimelineRepository>();
builder.Services.AddSingleton<LiveSessionRepository>();
builder.Services.AddSingleton<LiveEventTextIndexRepository>();
builder.Services.AddSingleton<MongoDbInitializer>();

builder.Services.AddSingleton<ICookiePoolService, CookiePoolService>();
builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();
builder.Services.AddSingleton<ILiveSessionTracker, LiveSessionTracker>();
builder.Services.AddSingleton<IAccountInfoCrawler, AccountInfoCrawler>();

builder.Services.AddSingleton<IBilibiliAccountProvider, BilibiliAccountProvider>();
builder.Services.AddSingleton<IBiliApiClient, BiliApiClient>();
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
    await initializer.EnsureIndexesAsync();
    await initializer.BackfillSpaceTimelinePubTsAsync();
    await initializer.BackfillGiftPriceAsync();
    await initializer.BackfillLiveEventTextIndexAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
