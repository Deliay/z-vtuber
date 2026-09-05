using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Notifier.Satori;
using Z.Vtuber.Notifier.Events;
using Z.Vtuber.Notifier.Dispatch;
using Z.Vtuber.Notifier.Controllers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

// 显式映射环境变量到 SatoriOptions
builder.Services.AddOptions<SatoriOptions>()
    .Configure(o =>
    {
        o.Endpoint = builder.Configuration["Z_VTUBER_SATORI_ENDPOINT"] ?? string.Empty;
        o.Token = builder.Configuration["Z_VTUBER_SATORI_TOKEN"];
        o.Platform = builder.Configuration["Z_VTUBER_SATORI_PLATFORM"];
        o.SelfId = builder.Configuration["Z_VTUBER_SATORI_SELF_ID"];
    })
    .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint),
        "缺少必需环境变量 Z_VTUBER_SATORI_ENDPOINT")
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<MongoDbInitializer>();
builder.Services.AddSingleton<INotificationSubscriptionRepository, NotificationSubscriptionRepository>();
// 使用 Singleton 工厂注册，统一 SatoriApiClient 缓存（登录发现结果在生命周期内共享）
builder.Services.AddSingleton<ISatoriApiClient>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SatoriOptions>>();
    var logger = sp.GetRequiredService<ILogger<SatoriApiClient>>();
    var httpClient = httpClientFactory.CreateClient(nameof(SatoriApiClient));
    return new SatoriApiClient(httpClient, options, logger);
});
builder.Services.AddHttpClient(nameof(SatoriApiClient));
builder.Services.AddHostedService<CookieInvalidEventSubscriber>();
builder.Services.AddSingleton<INotificationDispatchService, NotificationDispatchService>();

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
