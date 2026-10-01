using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 2.6：存量能力清洗幂等迁移（Testcontainers Mongo）——字段缺失补齐为输入 <c>["audio","text"]</c>/
/// 输出 <c>["text"]</c>；单侧缺失只补单侧；已设置不覆盖；marker 幂等重复执行零副作用。
/// </summary>
public class TrainingModelCapabilityBackfillTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingMongoSetup Setup => new(fixture.Database, NullLogger<TrainingMongoSetup>.Instance);

    private IMongoCollection<BsonDocument> Models =>
        fixture.Database.GetCollection<BsonDocument>(TrainingCollectionNames.TrainingModels);

    private static BsonDocument Doc(string model, BsonValue? input, BsonValue? output)
    {
        var doc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "ProviderId", ObjectId.GenerateNewId() },
            { "Model", model },
            { "Enabled", true },
            { "CreatedAt", DateTime.UtcNow },
            { "UpdatedAt", DateTime.UtcNow },
        };
        if (input is not null)
        {
            doc["InputCapabilities"] = input;
        }

        if (output is not null)
        {
            doc["OutputCapabilities"] = output;
        }

        return doc;
    }

    private async Task<BsonDocument> GetAsync(string model) =>
        await Models.Find(Builders<BsonDocument>.Filter.Eq("Model", model)).FirstAsync();

    [Fact]
    public async Task Backfill_FillsOnlyMissingFields()
    {
        await Models.InsertManyAsync(
        [
            Doc("both-missing", null, null),
            Doc("input-only", null, new BsonArray { "image" }),
            Doc("already-set", new BsonArray { "video" }, new BsonArray { "image" }),
        ]);

        await Setup.BackfillModelCapabilitiesAsync();

        var both = await GetAsync("both-missing");
        Assert.Equal(["audio", "text"], both["InputCapabilities"].AsBsonArray.Select(v => v.AsString).ToArray());
        Assert.Equal(["text"], both["OutputCapabilities"].AsBsonArray.Select(v => v.AsString).ToArray());

        // 单侧缺失只补输入、保留既有输出（不覆盖）。
        var inputOnly = await GetAsync("input-only");
        Assert.Equal(["audio", "text"], inputOnly["InputCapabilities"].AsBsonArray.Select(v => v.AsString).ToArray());
        Assert.Equal(["image"], inputOnly["OutputCapabilities"].AsBsonArray.Select(v => v.AsString).ToArray());

        // 已设置两侧均不覆盖。
        var set = await GetAsync("already-set");
        Assert.Equal(["video"], set["InputCapabilities"].AsBsonArray.Select(v => v.AsString).ToArray());
        Assert.Equal(["image"], set["OutputCapabilities"].AsBsonArray.Select(v => v.AsString).ToArray());
    }

    [Fact]
    public async Task Backfill_IdempotentByMarker()
    {
        await Models.InsertOneAsync(Doc("first-run", null, null));
        await Setup.BackfillModelCapabilitiesAsync();

        // 第二次执行：marker 已存在，跳过；新插入的缺失文档不被补齐（零副作用）。
        await Models.InsertOneAsync(Doc("second-run", null, null));
        await Setup.BackfillModelCapabilitiesAsync();

        var firstRun = await GetAsync("first-run");
        Assert.True(firstRun.Contains("InputCapabilities"));
        Assert.True(firstRun.Contains("OutputCapabilities"));
        Assert.True(await Setup.IsMigrationAppliedAsync(TrainingMongoSetup.ModelCapabilitiesBackfillMigrationKey));

        var secondRun = await GetAsync("second-run");
        Assert.False(secondRun.Contains("InputCapabilities"));
        Assert.False(secondRun.Contains("OutputCapabilities"));
    }
}
