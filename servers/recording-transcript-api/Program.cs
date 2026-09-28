using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.RecordingTranscript.Api;
using Z.Vtuber.RecordingTranscript.Api.Clients;
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

builder.Services.AddOptions<RecordingServiceOptions>()
    .Bind(builder.Configuration.GetSection(RecordingServiceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<IRecordingFileStatsClient, RecordingFileStatsClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["RecordingService:BaseUrl"] ?? RecordingServiceOptions.DefaultBaseUrl);
});

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:messaging");
    return ConnectionMultiplexer.Connect(connectionString);
});

builder.Services.AddSingleton<RecordingMongoSetup>();
    builder.Services.AddSingleton<TranscriptMongoSetup>();
    builder.Services.AddSingleton<LiveMongoSetup>();
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
    var recording = scope.ServiceProvider.GetRequiredService<RecordingMongoSetup>();
    var transcript = scope.ServiceProvider.GetRequiredService<TranscriptMongoSetup>();
    var live = scope.ServiceProvider.GetRequiredService<LiveMongoSetup>();
    await recording.EnsureIndexesAsync();
    await transcript.EnsureIndexesAsync();
    await live.EnsureIndexesAsync();
    await transcript.CleanupAsync();
}

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
