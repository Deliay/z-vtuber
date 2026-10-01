using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Training;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Emotion;
using Z.Vtuber.Training.FeaturePoints;
using Z.Vtuber.Training.Synthesis;
using Z.Vtuber.Training.Vectors;

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

// 音频生成：本地 embedding / Milvus 向量库 / 上游 TTS / 编排配置（ValidateOnStart）
builder.Services.AddOptions<EmbeddingOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MilvusOptions>()
    .Bind(builder.Configuration.GetSection(MilvusOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TtsOptions>()
    .Bind(builder.Configuration.GetSection(TtsOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => TtsOptions.SupportedLangs.Contains(
            o.DefaultLang?.Trim().ToUpperInvariant() ?? string.Empty, StringComparer.Ordinal),
        "Tts:DefaultLang must be one of ZH/EN/JA/ES/AR")
    .ValidateOnStart();

builder.Services.AddOptions<SynthesizeOptions>()
    .Bind(builder.Configuration.GetSection(SynthesizeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmotionTaskOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RecordingServiceOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MilvusOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<TtsOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SynthesizeOptions>>().Value);

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

// 本地 embedding（named client EmbeddingService，BaseAddress = Embedding:BaseUrl；超时经 EmbeddingOptions 控制）
builder.Services.AddHttpClient(LocalEmbeddingClient.ClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration[$"{EmbeddingOptions.SectionName}:BaseUrl"]
            ?? EmbeddingOptions.DefaultBaseUrl);
});

// Milvus REST（named client MilvusStore；禁用自动重定向）
builder.Services.AddHttpClient(MilvusRestClient.ClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

// 上游 index-tts-api（named client TtsService，BaseAddress = Tts:BaseUrl；IndexTtsClient 每次经 factory 取用）
builder.Services.AddHttpClient(IndexTtsClient.ClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration[$"{TtsOptions.SectionName}:BaseUrl"]
            ?? TtsOptions.DefaultBaseUrl);
});
builder.Services.AddSingleton<ITtsClient, IndexTtsClient>();

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
builder.Services.AddSingleton<IModelDiscoveryClient, ModelDiscoveryClient>();
builder.Services.AddSingleton<IEmotionTaskLockProvider, RedisEmotionTaskLockProvider>();
builder.Services.AddSingleton<EmotionTaskRunner>();
builder.Services.AddSingleton<EmotionTaskScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmotionTaskScanner>());

// 音频生成：embedding / Milvus 向量仓储 / 同步 / 编排 / TTS
builder.Services.AddSingleton<IEmbeddingClient, LocalEmbeddingClient>();
builder.Services.AddSingleton<IMilvusRestClient, MilvusRestClient>();
builder.Services.AddSingleton<IVectorStore, MilvusVectorStore>();
builder.Services.AddSingleton<VectorSyncService>();
builder.Services.AddSingleton<SegmentSyncService>();
builder.Services.AddSingleton<SynthesizeService>();

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

using (var scope = app.Services.CreateScope())
{
    var training = scope.ServiceProvider.GetRequiredService<TrainingMongoSetup>();
    var transcript = scope.ServiceProvider.GetRequiredService<TranscriptMongoSetup>();
    await training.EnsureIndexesAsync();
    await training.BackfillModelCapabilitiesAsync();
    await transcript.EnsureIndexesAsync();

    // Milvus 集合/索引幂等建立（best-effort：Milvus 暂不可用不阻断服务启动，可由 vectors/rebuild 兜底）
    try
    {
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await vectorStore.EnsureCollectionsAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Milvus collections initialization failed; synthesize unavailable until Milvus recovers");
    }
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
