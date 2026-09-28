using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Training;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Emotion;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("mongo");

builder.AddMongoDBClient("mongo");

// 情绪任务锁（training 首次引入 Redis，节 ConnectionStrings:messaging）
builder.AddRedisClient("messaging");

// AI 调用 / 情绪任务 / 音频取数配置（ValidateOnStart）
builder.Services.AddOptions<AiOptions>()
    .Bind(builder.Configuration.GetSection(AiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<EmotionTaskOptions>()
    .Bind(builder.Configuration.GetSection(EmotionTaskOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RecordingServiceOptions>()
    .Bind(builder.Configuration.GetSection(RecordingServiceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmotionTaskOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RecordingServiceOptions>>().Value);

// transcript-api 内网地址（named client，节 TranscriptService:BaseUrl）
builder.Services.AddHttpClient<ITranscriptApiClient, TranscriptApiClient>(
    TranscriptApiClient.ClientName, client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration[$"{TrainingOptions.SectionName}:BaseUrl"]
                ?? TrainingOptions.DefaultBaseUrl);
    });

// AI 模型调用 named client（无 BaseAddress，逐请求拼绝对 endpoint；超时经 AiOptions 控制）
builder.Services.AddHttpClient(ChatModelClientFactory.ClientName);

// recording 音频片段取数（typed client，BaseAddress 为 recording 服务地址）
builder.Services.AddHttpClient<IRecordingAudioClient, RecordingAudioClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration[$"{RecordingServiceOptions.SectionName}:BaseUrl"]
            ?? RecordingServiceOptions.DefaultBaseUrl);
});

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<TrainingMongoSetup>();
builder.Services.AddSingleton<TranscriptMongoSetup>();
builder.Services.AddSingleton<TrainingDatasetRepository>();
builder.Services.AddSingleton<TrainingDatasetItemRepository>();
builder.Services.AddSingleton<TrainingProviderRepository>();
builder.Services.AddSingleton<TrainingModelRepository>();
builder.Services.AddSingleton<TrainingEmotionTaskRepository>();

builder.Services.AddSingleton<IChatModelClientFactory, ChatModelClientFactory>();
builder.Services.AddSingleton<IModelDiscoveryClient, ModelDiscoveryClient>();
builder.Services.AddSingleton<IEmotionTaskLockProvider, RedisEmotionTaskLockProvider>();
builder.Services.AddSingleton<EmotionTaskRunner>();
builder.Services.AddSingleton<EmotionTaskScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmotionTaskScanner>());

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var training = scope.ServiceProvider.GetRequiredService<TrainingMongoSetup>();
    var transcript = scope.ServiceProvider.GetRequiredService<TranscriptMongoSetup>();
    await training.EnsureIndexesAsync();
    await transcript.EnsureIndexesAsync();
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
