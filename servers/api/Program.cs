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
builder.Services.AddSingleton<MongoDbInitializer>();

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
