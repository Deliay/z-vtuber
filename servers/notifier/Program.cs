using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Notifier.Milky;
using Z.Vtuber.Notifier.Events;
using Z.Vtuber.Notifier.Dispatch;
using Z.Vtuber.Notifier.Controllers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

// 显式映射环境变量到 MilkyOptions
builder.Services.AddOptions<MilkyOptions>()
    .Configure(o =>
    {
        o.Endpoint = builder.Configuration["Z_VTUBER_MILKY_ENDPOINT"] ?? string.Empty;
        o.AccessToken = builder.Configuration["Z_VTUBER_MILKY_ACCESS_TOKEN"] ?? string.Empty;
    })
    .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint),
        "缺少必需环境变量 Z_VTUBER_MILKY_ENDPOINT")
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<MongoDbInitializer>();
builder.Services.AddSingleton<INotificationSubscriptionRepository, NotificationSubscriptionRepository>();
// 注册 MilkyHttpClient 和 MilkyApiClient
builder.Services.AddSingleton<MilkyHttpClient>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MilkyOptions>>();
    return new MilkyHttpClient(options.Value);
});
builder.Services.AddSingleton<IMilkyApiClient>(sp =>
{
    var httpClient = sp.GetRequiredService<MilkyHttpClient>();
    var logger = sp.GetRequiredService<ILogger<MilkyApiClient>>();
    return new MilkyApiClient(httpClient, logger);
});
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
