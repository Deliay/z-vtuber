using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using Z.Vtuber.Cookie.Refresh;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Pool;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

builder.Services.AddOptions<CookieServiceOptions>()
    .Validate(o => !string.IsNullOrWhiteSpace(o.BrowserSessionDataDir),
        "缺少必需配置 BrowserSessionDataDir")
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<MongoDbInitializer>();
builder.Services.AddSingleton<ICookieSessionRepository, CookieSessionRepository>();
builder.Services.AddSingleton<IDistributedLockProvider, RedisDistributedLockProvider>();
builder.Services.AddSingleton<ICookiePoolService, CookiePoolService>();
builder.Services.AddSingleton<ICookieDispatchService, CookieDispatchService>();
builder.Services.AddSingleton<IInvalidationService, InvalidationService>();
builder.Services.AddSingleton<ICookieInvalidEventPublisher, RedisCookieInvalidEventPublisher>();
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
    return playwright.Chromium;
});

builder.Services.AddSingleton<IBrowserSessionManager, BrowserSessionManager>();

builder.Services.AddHostedService<SessionRefreshJob>();
builder.Services.AddHostedService<LoginSessionWorker>();

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<MongoDbInitializer>();
    await initializer.EnsureIndexesAsync();
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
