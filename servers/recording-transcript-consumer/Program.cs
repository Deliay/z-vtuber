using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.RecordingTranscript.Consumer;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
using Z.Vtuber.RecordingTranscript.Consumer.Context;
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

builder.Services.AddSingleton<RecordingMongoSetup>();
    builder.Services.AddSingleton<TranscriptMongoSetup>();
    builder.Services.AddSingleton<LiveMongoSetup>();

builder.Services.AddSingleton<RecordingRepository>();
builder.Services.AddSingleton<TranscriptTaskRepository>();
builder.Services.AddSingleton<TranscriptSegmentRepository>();
builder.Services.AddSingleton<TranscriptModelRepository>();
builder.Services.AddSingleton<TranscriptAutoEnqueueRepository>();
builder.Services.AddSingleton<RecordingStatusRepository>();
builder.Services.AddSingleton<AccountBasicInfoRepository>();
builder.Services.AddSingleton<LiveGuardRepository>();
builder.Services.AddSingleton<LiveEventTextIndexRepository>();

builder.Services.AddSingleton<TranscriptModelMigration>();
builder.Services.AddSingleton<ITranscriptContextBuilder, TranscriptContextBuilder>();
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
    var recording = scope.ServiceProvider.GetRequiredService<RecordingMongoSetup>();
    var transcript = scope.ServiceProvider.GetRequiredService<TranscriptMongoSetup>();
    var live = scope.ServiceProvider.GetRequiredService<LiveMongoSetup>();
    await recording.EnsureIndexesAsync();
    await transcript.EnsureIndexesAsync();
    await live.EnsureIndexesAsync();
    await transcript.CleanupAsync();

    var migration = scope.ServiceProvider.GetRequiredService<TranscriptModelMigration>();
    await migration.MigrateAsync();
}

app.MapHealthChecks("/health");

app.Run();
