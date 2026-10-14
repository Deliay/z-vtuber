using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Training;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Emotion;
using Z.Vtuber.Training.FeaturePoints;
using Z.Vtuber.Training.ImageGeneration;
using Z.Vtuber.Training.Synthesis;

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

// 音频生成：语音合成语种配置（ValidateOnStart）
builder.Services.AddOptions<SpeechOptions>()
    .Bind(builder.Configuration.GetSection(SpeechOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => SpeechOptions.SupportedLangs.Contains(
            o.DefaultLang?.Trim().ToUpperInvariant() ?? string.Empty, StringComparer.Ordinal),
        "Speech:DefaultLang must be one of ZH/EN/JA/ES/AR")
    .ValidateOnStart();

builder.Services.AddOptions<SynthesizeOptions>()
    .Bind(builder.Configuration.GetSection(SynthesizeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// 绘图生成（图片大小上限 / 上游图片生成超时）
builder.Services.AddOptions<ImageOptions>()
    .Bind(builder.Configuration.GetSection(ImageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmotionTaskOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RecordingServiceOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SpeechOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SynthesizeOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ImageOptions>>().Value);

// transcript-api 内网地址（named client，节 TranscriptService:BaseUrl）
builder.Services.AddHttpClient<ITranscriptApiClient, TranscriptApiClient>(
    TranscriptApiClient.ClientName, client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration[$"{TrainingOptions.SectionName}:BaseUrl"]
                ?? TrainingOptions.DefaultBaseUrl);
    });

// AI 模型调用 named client（无 BaseAddress，逐请求拼绝对 endpoint；超时经 AiOptions 控制）。
// 禁用自动重定向：endpoint 为用户可配的任意 http(s) 地址，避免携带存储的 API key 跟随 3xx 跳到其他主机（SSRF/凭据泄漏）。
// 固定请求头 x-opencode-session（agent 标识 + 随机 guid）经 message handler 加到该 client 的每次调用上，
// 覆盖情绪识别 / 重新识别 / 模型发现，与调用场景无关。
builder.Services.AddTransient<ModelSessionHeaderHandler>();
builder.Services.AddHttpClient(ChatModelClientFactory.ClientName)
    .AddHttpMessageHandler<ModelSessionHeaderHandler>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

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
builder.Services.AddSingleton<TrainingDatasetItemSegmentRepository>();
builder.Services.AddSingleton<TrainingProviderRepository>();
builder.Services.AddSingleton<TrainingModelRepository>();
builder.Services.AddSingleton<TrainingEmotionTaskRepository>();
builder.Services.AddSingleton<TrainingFeaturePointRepository>();
builder.Services.AddSingleton<ITrainingModelResolver, TrainingModelResolver>();

builder.Services.AddSingleton<IChatModelClientFactory, ChatModelClientFactory>();
builder.Services.AddSingleton<IImageModelClientFactory, ImageModelClientFactory>();
builder.Services.AddSingleton<ISpeechModelClientFactory, SpeechModelClientFactory>();
builder.Services.AddSingleton<IModelDiscoveryClient, ModelDiscoveryClient>();
builder.Services.AddSingleton<IEmotionTaskLockProvider, RedisEmotionTaskLockProvider>();
builder.Services.AddSingleton<EmotionTaskRunner>();
builder.Services.AddSingleton<EmotionTaskScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmotionTaskScanner>());

// 音频生成：候选短句同步 / 编排
builder.Services.AddSingleton<SegmentSyncService>();
builder.Services.AddSingleton<SynthesizeService>();
builder.Services.AddSingleton<ImageGenerateService>();

builder.Services.AddControllers();

var app = builder.Build();

// Synthesize:DefaultEmotionModelId 本期移除（由功能点 emotion-from-text 的「指定模型」承接）：
// 残留配置非空则 Warn 一次并忽略，不影响启动。
var legacyEmotionModelId = builder.Configuration[
    $"{SynthesizeOptions.SectionName}:DefaultEmotionModelId"];
if (!string.IsNullOrWhiteSpace(legacyEmotionModelId))
{
    app.Logger.LogWarning(
        "Synthesize:DefaultEmotionModelId is ignored (removed); configure the emotion-from-text feature point model instead.");
}

// Tts / Milvus / Embedding 配置节本期整体移除（原 TTS 下游与 Milvus/Ollama 向量链路下线）：
// 残留任一非空则 Warn 一次并忽略，不影响启动。
foreach (var legacySection in new[] { "Tts", "Milvus", "Embedding" })
{
    if (builder.Configuration.GetSection(legacySection).GetChildren().Any())
    {
        app.Logger.LogWarning(
            "{Section} configuration is ignored (removed); speech synthesis uses the speech-synthesize feature point model instead.",
            legacySection);
    }
}

using (var scope = app.Services.CreateScope())
{
    var training = scope.ServiceProvider.GetRequiredService<TrainingMongoSetup>();
    var transcript = scope.ServiceProvider.GetRequiredService<TranscriptMongoSetup>();
    await training.EnsureIndexesAsync();
    await training.BackfillModelCapabilitiesAsync();
    await transcript.EnsureIndexesAsync();
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
