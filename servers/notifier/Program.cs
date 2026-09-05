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

builder.Services.AddOptions<SatoriOptions>()
    .Bind(builder.Configuration.GetSection(SatoriOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint),
        "缺少必需环境变量 Z_VTUBER_SATORI_ENDPOINT")
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<MongoDbInitializer>();
builder.Services.AddSingleton<INotificationSubscriptionRepository, NotificationSubscriptionRepository>();
builder.Services.AddSingleton<ISatoriApiClient, SatoriApiClient>();
builder.Services.AddSingleton<ICookieInvalidEventSubscriber, CookieInvalidEventSubscriber>();
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
