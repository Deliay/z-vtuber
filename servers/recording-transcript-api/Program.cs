using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.RecordingTranscript.Api;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Sse;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("mongo");

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

builder.Services.AddOptions<TranscriptApiOptions>()
    .BindConfiguration(TranscriptApiOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:messaging");
    return ConnectionMultiplexer.Connect(connectionString);
});

builder.Services.AddSingleton<MongoDbInitializer>();
builder.Services.AddSingleton<AccountBasicInfoRepository>();
builder.Services.AddSingleton<RecordingRepository>();
builder.Services.AddSingleton<TranscriptTaskRepository>();
builder.Services.AddSingleton<TranscriptSegmentRepository>();
builder.Services.AddSingleton<TranscriptModelRepository>();
builder.Services.AddSingleton<TranscriptAutoEnqueueRepository>();
builder.Services.AddSingleton<TranscriptSegmentStream>();

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var mongoClient = scope.ServiceProvider.GetRequiredService<IMongoClient>();
    await EnsureReplicaSetAsync(mongoClient, app.Logger);

    var initializer = scope.ServiceProvider.GetRequiredService<MongoDbInitializer>();
    await initializer.EnsureIndexesAsync();
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();

// 事务前置校验：MongoDB 必须是 replica set（否则 segment+position 事务永久失败且无显式报错）。
// 复用旧 standalone 数据卷时 rs-init.js 不会执行，副本集无 primary；这里给出清晰错误而非静默失败。
static async Task EnsureReplicaSetAsync(IMongoClient client, ILogger logger)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    Exception? lastError = null;

    while (true)
    {
        try
        {
            var hello = await client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
            if (hello.Contains("setName"))
            {
                return;
            }

            lastError = new InvalidOperationException("MongoDB is running as a standalone server.");
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        if (DateTime.UtcNow >= deadline)
        {
            logger.LogCritical(lastError,
                "MongoDB 不是 replica set，事务不可用。最可能原因：复用了旧版 AppHost 的 standalone 数据卷，"
                + "rs-init.js 仅在数据目录为空时执行，副本集始终没有 primary。"
                + "请删除/重建 Mongo 数据卷（或手动执行 rs.initiate）后重启；详见 infra/apphost/AppHost.cs。");
            throw new InvalidOperationException(
                "MongoDB replica set check failed; transactions are unavailable.", lastError);
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}
