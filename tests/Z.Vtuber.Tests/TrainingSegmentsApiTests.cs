using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>候选短句反向索引重建端点（控制器直测 + 真 Mongo）：200 rebuilt / 404 dataset not found。</summary>
public class TrainingSegmentsApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private SegmentSyncService Segments =>
        new(Items, new TrainingDatasetItemSegmentRepository(fixture.Database),
            NullLogger<SegmentSyncService>.Instance);

    private TrainingSegmentsController CreateController() =>
        new(Datasets, Segments, NullLogger<TrainingSegmentsController>.Instance);

    private async Task<ObjectId> SeedDatasetAsync()
    {
        var now = DateTime.UtcNow;
        var record = await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = $"ds-{Guid.NewGuid():N}", Type = "audio", CreatedAt = now, UpdatedAt = now,
        });
        return record.Id;
    }

    private async Task SeedItemAsync(ObjectId datasetId, string text)
    {
        var now = DateTime.UtcNow;
        await Items.InsertManyIgnoreDuplicatesAsync(
        [
            new TrainingDatasetItemRecord
            {
                DatasetId = datasetId,
                AudioType = "transcript",
                Source = "task",
                AudioUri = $"b/live/1/recordings/{Guid.NewGuid():N}.flv",
                AudioKey = $"k-{Guid.NewGuid():N}",
                StartMs = 0,
                EndMs = 1000,
                Text = text,
                CreatedAt = now,
                UpdatedAt = now,
            },
        ]);
    }

    [Fact]
    public async Task Rebuild_ReturnsRebuiltCount()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "你好，世界。");
        await SeedItemAsync(datasetId, "第三句");

        var result = await CreateController().Rebuild(datasetId.ToString());

        var payload = Assert.IsType<TrainingSegmentsRebuildResultDto>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(3, payload.Rebuilt);
    }

    [Fact]
    public async Task Rebuild_UnknownDataset_Returns404()
    {
        var result = await CreateController().Rebuild(ObjectId.GenerateNewId().ToString());

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        Assert.Equal("dataset not found", Assert.IsType<ErrorResponse>(objectResult.Value).Error);
    }
}
