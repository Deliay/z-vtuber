using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;
using Z.Vtuber.Recording.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<Z.Vtuber.Recording.MongoDbHealthCheck>("mongo");

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

builder.Services.AddOptions<RecordingOptions>()
    .Bind(builder.Configuration.GetSection(RecordingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<RecordingOptions>>().Value);

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<MongoDbInitializer>();
builder.Services.AddSingleton<AccountBasicInfoRepository>();
builder.Services.AddSingleton<RecordingStatusRepository>();
builder.Services.AddSingleton<RecordingRepository>();
builder.Services.AddSingleton<IOrphanRecordingFinalizer, OrphanRecordingFinalizer>();
builder.Services.AddSingleton<LiveSessionRepository>();
builder.Services.AddSingleton<LiveStatusRepository>();
builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();

builder.Services.AddSingleton(sp =>
    new AudioStreamHub(sp.GetRequiredService<RecordingOptions>().AudioBufferBytes));
builder.Services.AddSingleton<RecordingStatusReporter>();

builder.Services.AddHttpClient<IBiliLiveApi, BiliLiveApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IStreamBitrateProbe, FfprobeStreamBitrateProbe>();
builder.Services.AddHttpClient<IRecordingCookieClient, CookieServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["CookieService:BaseUrl"] ?? "http://cookie-service");
});

builder.Services.AddSingleton<IRecordingProcessFactory, FfmpegRecordingProcessFactory>();
builder.Services.AddSingleton<IRecordingLockProvider, RedisRecordingLockProvider>();
builder.Services.AddSingleton<IRecordingEventPublisher, RedisRecordingEventPublisher>();
builder.Services.AddSingleton<RecordingBackfillMigration>();
builder.Services.AddSingleton<RecordingSession>();
builder.Services.AddSingleton<IRecordingSessionRunner>(sp => sp.GetRequiredService<RecordingSession>());

builder.Services.AddHostedService<LiveStatusPoller>();

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<MongoDbInitializer>();
    await initializer.EnsureIndexesAsync();

    // 存量清洗：失败不阻断启动（幂等 marker 保证后续重试）
    try
    {
        var backfill = scope.ServiceProvider.GetRequiredService<RecordingBackfillMigration>();
        await backfill.RunAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Recording backfill migration failed; continuing startup.");
    }

    // 启动时幂等收尾上次进程异常退出遗留的孤儿场次（EndedAt=null），恢复 enqueue/自动识别链路
    try
    {
        var orphanFinalizer = scope.ServiceProvider.GetRequiredService<IOrphanRecordingFinalizer>();
        await orphanFinalizer.FinalizeAllOpenAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Orphan recording finalization failed; continuing startup.");
    }
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
