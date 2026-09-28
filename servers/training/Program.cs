using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Training;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<MongoDbHealthCheck>("mongo");

builder.AddMongoDBClient("mongo");

// transcript-api 内网地址（named client，节 TranscriptService:BaseUrl）
builder.Services.AddHttpClient<ITranscriptApiClient, TranscriptApiClient>(
    TranscriptApiClient.ClientName, client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration[$"{TrainingOptions.SectionName}:BaseUrl"]
                ?? TrainingOptions.DefaultBaseUrl);
    });

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

builder.Services.AddSingleton<TrainingMongoSetup>();
    builder.Services.AddSingleton<TranscriptMongoSetup>();
builder.Services.AddSingleton<TrainingDatasetRepository>();
builder.Services.AddSingleton<TrainingDatasetItemRepository>();

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
