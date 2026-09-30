using MongoDB.Driver;
using System.Text.Json.Serialization;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Live;

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
builder.AddRedisClient("messaging");

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<AccountBasicInfoRepository>();
builder.Services.AddSingleton<LiveStatusRepository>();
builder.Services.AddSingleton<LiveEventRepository>();
builder.Services.AddSingleton<SpaceTimelineRepository>();
builder.Services.AddSingleton<LiveSessionRepository>();
builder.Services.AddSingleton<LiveEventTextIndexRepository>();
builder.Services.AddSingleton<LiveGuardRepository>();
builder.Services.AddSingleton<RecordingStatusRepository>();
builder.Services.AddSingleton<LiveMongoSetup>();
    builder.Services.AddSingleton<CookieMongoSetup>();
    builder.Services.AddSingleton<WeiboMongoSetup>();

builder.Services.AddBackstageDataAggregation();

builder.Services.AddSingleton<WeiboSubscriptionRepository>();
builder.Services.AddSingleton<WeiboTimelineRepository>();
builder.Services.AddSingleton<IWeiboSubscriptionService, WeiboSubscriptionService>();

builder.Services.AddSingleton<ICookiePoolService, CookiePoolService>();
builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();

// 订阅事件跨进程广播：api 进程内订阅成功后发布到 Redis，crawler 侧即时抓取主播资料
builder.Services.AddSubscriptionEvents();

builder.Services.AddSingleton<ILiveEventBus, LiveEventBus>();

builder.Services.AddHttpClient(Z.Vtuber.Controllers.BilibiliLiveRecordingController.RecordingClientName, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["RecordingService:BaseUrl"] ?? "http://recording");
});

builder.Services.AddHttpClient(Z.Vtuber.Controllers.RecordingTranscriptProxyController.TranscriptClientName, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["TranscriptService:BaseUrl"] ?? "http://recording-transcript-api");
});

// 代理超时须**高于** training 各阶段上游预算（Tts/Ai/Embedding RequestTimeoutSeconds=120），
// 否则长耗时端点（synthesize / vectors/rebuild / items/import）在 training 正常完成时被代理先判超时 → 误报 502。
builder.Services.AddHttpClient(Z.Vtuber.Controllers.TrainingProxyController.TrainingClientName, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["TrainingService:BaseUrl"] ?? "http://training");
    client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue(
        "TrainingService:RequestTimeoutSeconds",
        Z.Vtuber.Controllers.TrainingProxyController.DefaultTimeoutSeconds));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var live = scope.ServiceProvider.GetRequiredService<LiveMongoSetup>();
    var cookie = scope.ServiceProvider.GetRequiredService<CookieMongoSetup>();
    var weibo = scope.ServiceProvider.GetRequiredService<WeiboMongoSetup>();

    // Phase 4.2: 启动顺序固定为「删旧 Mid 唯一索引 → 迁移 → 建 (Mid,Type) 复合唯一索引」
    // 1-3. Live 迁移序列（删旧 Mid 唯一索引 → 存量迁移 → 建 (Mid, Type) 复合唯一索引）
    await live.MigrateAsync();

    // 4. 各领域索引初始化（保持拆分前原顺序：Live → Cookie → Weibo）
    await live.EnsureIndexesAsync();
    await cookie.EnsureIndexesAsync();
    await weibo.EnsureIndexesAsync();

    // 5. Live 域回填（不含需抢占锁的 payload object 回填）
    await live.BackfillAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
