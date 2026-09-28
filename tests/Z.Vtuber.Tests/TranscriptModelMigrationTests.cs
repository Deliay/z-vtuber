using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Services;

namespace Z.Vtuber.Tests;

public class TranscriptModelMigrationTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptModelRepository Models => new(mongo.Database);

    private IMongoCollection<MetaRecord> Meta =>
        mongo.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);

    private TranscriptModelMigration Migration(string? asrModel = null) =>
        new(mongo.Database,
            new ConfigurationBuilder()
                .AddInMemoryCollection(asrModel is null
                    ? []
                    : new Dictionary<string, string?> { ["TranscriptConsumer:AsrModel"] = asrModel })
                .Build(),
            NullLogger<TranscriptModelMigration>.Instance);

    private Task<ObjectId> InsertModelAsync(string? model) =>
        Models.InsertAsync(new TranscriptModelRecord
        {
            Name = "model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key",
            Model = model ?? string.Empty,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task Migrate_BackfillsMissingModelFromConfiguration()
    {
        var id = await InsertModelAsync(null);

        await Migration("fun-asr-realtime").MigrateAsync();

        var record = await Models.GetByIdAsync(id);
        Assert.Equal("fun-asr-realtime", record!.Model);
        Assert.True(await Meta.Find(r => r.Key == TranscriptModelMigration.MigrationKey).AnyAsync());
    }

    [Fact]
    public async Task Migrate_UsesDefaultModelWhenConfigurationMissing()
    {
        var id = await InsertModelAsync(null);

        await Migration().MigrateAsync();

        Assert.Equal(TranscriptModelMigration.DefaultModel, (await Models.GetByIdAsync(id))!.Model);
    }

    [Fact]
    public async Task Migrate_DoesNotOverwriteAlreadyFilledModel()
    {
        var id = await InsertModelAsync("explicit-model");

        await Migration("fun-asr-realtime").MigrateAsync();

        Assert.Equal("explicit-model", (await Models.GetByIdAsync(id))!.Model);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        var id = await InsertModelAsync(null);

        await Migration("fun-asr-realtime").MigrateAsync();
        // 回滚场景：marker 存在但仍有缺失记录时重新迁移
        await Migration("other-model").MigrateAsync();

        Assert.Equal("fun-asr-realtime", (await Models.GetByIdAsync(id))!.Model);
    }
}
