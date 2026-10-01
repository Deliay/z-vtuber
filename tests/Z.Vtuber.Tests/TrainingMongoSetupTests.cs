using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Training 域索引初始化。</summary>
public class TrainingMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EnsureIndexes_CreatesExpectedIndexes()
    {
        var setup = new TrainingMongoSetup(fixture.Database, NullLogger<TrainingMongoSetup>.Instance);
        await setup.EnsureIndexesAsync();

        var datasets = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TrainingDatasetRecord>(TrainingCollectionNames.TrainingDatasets));
        Assert.Contains("Type_1_Name_1", datasets);

        var items = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TrainingDatasetItemRecord>(TrainingCollectionNames.TrainingDatasetItems));
        Assert.Contains("DatasetId_1_AudioKey_1", items);
        Assert.Contains("DatasetId_1_AudioType_1", items);
        Assert.Contains("DatasetId_1_Source_1", items);

        // 返工 #9：Provider/Model/EmotionTask 三集合索引同步断言
        var providers = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TrainingProviderRecord>(TrainingCollectionNames.TrainingProviders));
        Assert.Contains("Name_1", providers);

        var models = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TrainingModelRecord>(TrainingCollectionNames.TrainingModels));
        Assert.Contains("ProviderId_1_Model_1", models);
        Assert.Contains("ProviderId_1", models);

        var emotionTasks = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TrainingEmotionTaskRecord>(TrainingCollectionNames.TrainingEmotionTasks));
        Assert.Contains("Status_1_CreatedAt_1", emotionTasks);
        Assert.Contains("DatasetId_1_CreatedAt_1", emotionTasks);
        Assert.Contains("ItemId_1", emotionTasks);
        // 返工：条目活跃任务部分唯一索引（并发重复提交幂等兜底）
        Assert.Contains("ItemId_1_Active", emotionTasks);

        // 候选短句反向索引：按数据集加载 / 条目级替换
        var segments = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TrainingDatasetItemSegmentRecord>(
                TrainingCollectionNames.TrainingDatasetItemSegments));
        Assert.Contains("DatasetId_1", segments);
        Assert.Contains("DatasetId_1_ItemId_1", segments);
    }

    [Fact]
    public async Task EnsureIndexes_NameUniqueIndex_RejectsDuplicateProviderName()
    {
        var setup = new TrainingMongoSetup(fixture.Database, NullLogger<TrainingMongoSetup>.Instance);
        await setup.EnsureIndexesAsync();
        var repository = new TrainingProviderRepository(fixture.Database);
        var now = DateTime.UtcNow;

        await repository.InsertAsync(new TrainingProviderRecord
        {
            Name = "dup", Type = "openai", Endpoint = "https://x", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });

        await Assert.ThrowsAsync<MongoDB.Driver.MongoWriteException>(() => repository.InsertAsync(
            new TrainingProviderRecord
            {
                Name = "dup", Type = "claude", Endpoint = "https://y", ApiKey = "k",
                CreatedAt = now, UpdatedAt = now,
            }));
    }
}
