using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MongoDB.Driver;
using StackExchange.Redis;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Locking;
using Z.Vtuber.Cookie.Persistence;
using Z.Vtuber.Cookie.Platform;
using Z.Vtuber.Cookie.Refresh;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Pool;

var builder = WebApplication.CreateBuilder(args);

const string Bilibili = "bilibili";
const string Weibo = "weibo";

builder.Services.AddHealthChecks();

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

builder.Services.AddOptions<CookieServiceOptions>()
    .BindConfiguration(CookieServiceOptions.SectionName)
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<CookieServiceOptions>, CookieServiceOptionsValidator>();

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<CookieMongoSetup>();
builder.Services.AddSingleton<IDistributedLockProvider, RedisDistributedLockProvider>();
builder.Services.AddSingleton<ICookieAccountInfoRepository, CookieAccountInfoRepository>();

// Playwright
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:messaging");
    return ConnectionMultiplexer.Connect(connectionString);
});

builder.Services.AddSingleton<IBrowserType>(sp =>
{
    var playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
    return playwright.Firefox;
});

var bilibiliProfile = new BilibiliCookiePlatformProfile();
var weiboProfile = new WeiboCookiePlatformProfile();

builder.Services.AddSingleton<ICookiePlatformProfile>(bilibiliProfile);
builder.Services.AddKeyedSingleton<ICookiePlatformProfile>(Weibo, weiboProfile);

// Bilibili：保持既有非 keyed 注册，行为与锁 key 不变
builder.Services.AddSingleton<IBrowserSessionManager>(sp => new BrowserSessionManager(
    bilibiliProfile,
    sp.GetRequiredService<IBrowserType>(),
    sp.GetRequiredService<IOptions<CookieServiceOptions>>(),
    sp.GetRequiredService<ILogger<BrowserSessionManager>>()));
builder.Services.AddSingleton<ICookieSessionRepository>(sp => new CookieSessionRepository(
    sp.GetRequiredService<IMongoDatabase>(), CookieCollectionNames.CookieSessions));
builder.Services.AddSingleton<ICookiePoolService>(sp => new CookiePoolService(
    sp.GetRequiredService<IMongoDatabase>(), CookieCollectionNames.Cookies));
builder.Services.AddSingleton<ICookieDispatchService>(sp => new CookieDispatchService(
    sp.GetRequiredService<IMongoDatabase>(), CookieCollectionNames.Cookies, CookieCollectionNames.CookieSessions));
builder.Services.AddSingleton<ICookieInvalidEventPublisher>(sp => new RedisCookieInvalidEventPublisher(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    Bilibili,
    sp.GetRequiredService<ICookieAccountInfoRepository>(),
    sessionRepository: null,
    sp.GetRequiredService<ILogger<RedisCookieInvalidEventPublisher>>()));
builder.Services.AddSingleton<IInvalidationService>(sp => new InvalidationService(
    bilibiliProfile,
    sp.GetRequiredService<ICookieSessionRepository>(),
    sp.GetRequiredService<ICookiePoolService>(),
    sp.GetRequiredService<IDistributedLockProvider>(),
    sp.GetRequiredService<IBrowserSessionManager>(),
    sp.GetRequiredService<ICookieInvalidEventPublisher>(),
    sp.GetRequiredService<IOptions<CookieServiceOptions>>(),
    sp.GetRequiredService<ILogger<InvalidationService>>()));

// Weibo：keyed 注册，独立集合与锁空间
builder.Services.AddKeyedSingleton<IBrowserSessionManager>(Weibo, (sp, _) => new BrowserSessionManager(
    weiboProfile,
    sp.GetRequiredService<IBrowserType>(),
    sp.GetRequiredService<IOptions<CookieServiceOptions>>(),
    sp.GetRequiredService<ILogger<BrowserSessionManager>>()));
builder.Services.AddKeyedSingleton<ICookieSessionRepository>(Weibo, (sp, _) => new CookieSessionRepository(
    sp.GetRequiredService<IMongoDatabase>(), CookieCollectionNames.WeiboCookieSessions));
builder.Services.AddKeyedSingleton<ICookiePoolService>(Weibo, (sp, _) => new CookiePoolService(
    sp.GetRequiredService<IMongoDatabase>(), CookieCollectionNames.WeiboCookies));
builder.Services.AddKeyedSingleton<ICookieDispatchService>(Weibo, (sp, _) => new CookieDispatchService(
    sp.GetRequiredService<IMongoDatabase>(), CookieCollectionNames.WeiboCookies, CookieCollectionNames.WeiboCookieSessions));
builder.Services.AddKeyedSingleton<ICookieInvalidEventPublisher>(Weibo, (sp, _) => new RedisCookieInvalidEventPublisher(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    Weibo,
    accountInfoRepository: null,
    sp.GetRequiredKeyedService<ICookieSessionRepository>(Weibo),
    sp.GetRequiredService<ILogger<RedisCookieInvalidEventPublisher>>()));
builder.Services.AddKeyedSingleton<IInvalidationService>(Weibo, (sp, _) => new InvalidationService(
    weiboProfile,
    sp.GetRequiredKeyedService<ICookieSessionRepository>(Weibo),
    sp.GetRequiredKeyedService<ICookiePoolService>(Weibo),
    sp.GetRequiredService<IDistributedLockProvider>(),
    sp.GetRequiredKeyedService<IBrowserSessionManager>(Weibo),
    sp.GetRequiredKeyedService<ICookieInvalidEventPublisher>(Weibo),
    sp.GetRequiredService<IOptions<CookieServiceOptions>>(),
    sp.GetRequiredService<ILogger<InvalidationService>>()));

// 平台上下文注册表（后台任务覆盖双平台）
builder.Services.AddKeyedSingleton<CookiePlatformContext>(Bilibili, (sp, _) => new CookiePlatformContext
{
    Profile = bilibiliProfile,
    SessionRepository = sp.GetRequiredService<ICookieSessionRepository>(),
    CookiePool = sp.GetRequiredService<ICookiePoolService>(),
    BrowserSessionManager = sp.GetRequiredService<IBrowserSessionManager>(),
    EventPublisher = sp.GetRequiredService<ICookieInvalidEventPublisher>(),
});
builder.Services.AddKeyedSingleton<CookiePlatformContext>(Weibo, (sp, _) => new CookiePlatformContext
{
    Profile = weiboProfile,
    SessionRepository = sp.GetRequiredKeyedService<ICookieSessionRepository>(Weibo),
    CookiePool = sp.GetRequiredKeyedService<ICookiePoolService>(Weibo),
    BrowserSessionManager = sp.GetRequiredKeyedService<IBrowserSessionManager>(Weibo),
    EventPublisher = sp.GetRequiredKeyedService<ICookieInvalidEventPublisher>(Weibo),
});
builder.Services.AddSingleton<CookiePlatformRegistry>(sp => new CookiePlatformRegistry(
    sp.GetRequiredKeyedService<CookiePlatformContext>(Bilibili),
    sp.GetRequiredKeyedService<CookiePlatformContext>(Weibo)));

builder.Services.AddSingleton<SessionRefreshJob>(sp => new SessionRefreshJob(
    sp.GetRequiredService<CookiePlatformRegistry>(),
    sp.GetRequiredService<IDistributedLockProvider>(),
    sp.GetRequiredService<IOptions<CookieServiceOptions>>(),
    sp.GetRequiredService<ILogger<SessionRefreshJob>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionRefreshJob>());

builder.Services.AddSingleton<LoginSessionWorker>(sp => new LoginSessionWorker(
    sp.GetRequiredService<CookiePlatformRegistry>(),
    sp.GetRequiredService<IDistributedLockProvider>(),
    sp.GetRequiredService<IOptions<CookieServiceOptions>>(),
    sp.GetRequiredService<ILogger<LoginSessionWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<LoginSessionWorker>());

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var cookie = scope.ServiceProvider.GetRequiredService<CookieMongoSetup>();
    await cookie.EnsureIndexesAsync();
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
