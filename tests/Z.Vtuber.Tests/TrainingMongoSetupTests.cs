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
    }
}
