using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.RecordingTranscript.Consumer;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;
using Z.Vtuber.RecordingTranscript.Consumer.Events;
using Z.Vtuber.RecordingTranscript.Consumer.Reconcile;
using Z.Vtuber.RecordingTranscript.Consumer.Services;
using Z.Vtuber.RecordingTranscript.Consumer.Tasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("mongo");

builder.AddMongoDBClient("mongo");
builder.AddRedisClient("messaging");

builder.Services.AddOptions<TranscriptConsumerOptions>()
    .Bind(builder.Configuration.GetSection(TranscriptConsumerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RecordingServiceOptions>()
    .Bind(builder.Configuration.GetSection(RecordingServiceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<TranscriptConsumerOptions>>().Value);
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<RecordingServiceOptions>>().Value);

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<MongoDbInitializer>();

builder.Services.AddSingleton<RecordingRepository>();
builder.Services.AddSingleton<TranscriptTaskRepository>();
builder.Services.AddSingleton<TranscriptSegmentRepository>();
builder.Services.AddSingleton<TranscriptModelRepository>();
builder.Services.AddSingleton<TranscriptAutoEnqueueRepository>();
builder.Services.AddSingleton<RecordingStatusRepository>();

builder.Services.AddSingleton<TranscriptEnqueueService>();
builder.Services.AddSingleton<ITranscriptTaskLockProvider, RedisTranscriptTaskLockProvider>();
builder.Services.AddSingleton<ITranscriptSegmentPersister, MongoTranscriptSegmentPersister>();
builder.Services.AddSingleton<ITranscriptSegmentPublisher, RedisTranscriptSegmentPublisher>();
builder.Services.AddSingleton<IAudioProcessFactory, FfmpegAudioProcessFactory>();
builder.Services.AddSingleton<IAudioSourceFactory, FfmpegAudioSourceFactory>();
builder.Services.AddSingleton<IStreamingAsrServiceFactory, AliFunAsrServiceFactory>();

builder.Services.AddHttpClient<IRecordingServiceClient, RecordingServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["RecordingService:BaseUrl"] ?? RecordingServiceOptions.DefaultBaseUrl);
});

builder.Services.AddSingleton<TranscriptTaskRunner>();
builder.Services.AddSingleton<ITranscriptTaskRunner>(sp => sp.GetRequiredService<TranscriptTaskRunner>());
builder.Services.AddSingleton<TranscriptTaskScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptTaskScanner>());
builder.Services.AddSingleton<TranscriptReconciler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptReconciler>());
builder.Services.AddHostedService<RecordingStartedSubscriber>();
builder.Services.AddHostedService<RecordingFinishedSubscriber>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var mongoClient = scope.ServiceProvider.GetRequiredService<IMongoClient>();
    await EnsureReplicaSetAsync(mongoClient, app.Logger);

    var initializer = scope.ServiceProvider.GetRequiredService<MongoDbInitializer>();
    await initializer.EnsureIndexesAsync();
}

app.MapHealthChecks("/health");

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
