using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 候选短句反向索引同步：rebuild 清空重建、按数据集加载、条目增量替换、批量/数据集删除级联。
/// </summary>
public class SegmentSyncServiceTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetItemSegmentRepository Segments => new(fixture.Database);

    private SegmentSyncService Create() =>
        new(Items, Segments, NullLogger<SegmentSyncService>.Instance);

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(ObjectId datasetId, string text, string? audioKey = null)
    {
        var now = DateTime.UtcNow;
        var record = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task",
            AudioUri = $"b/live/1/recordings/{Guid.NewGuid():N}.flv",
            AudioKey = audioKey ?? $"k-{Guid.NewGuid():N}",
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([record]);
        return record;
    }

    [Fact]
    public async Task Rebuild_BuildsSegmentsForNonEmptyText_AndSkipsEmpty()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "你好，世界。");
        await SeedItemAsync(datasetId, "   ");
        var service = Create();

        var rebuilt = await service.RebuildAsync(datasetId);

        Assert.Equal(2, rebuilt);
        var segments = await Segments.ListByDatasetAsync(datasetId);
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(first.Id, s.ItemId));
        Assert.Equal(new[] { "你好", "世界" }, segments.OrderBy(s => s.SegmentIndex).Select(s => s.Text));
        Assert.Equal(PinyinConverter.ToPinyin("你好"), segments.OrderBy(s => s.SegmentIndex).First().Pinyin);
    }

    [Fact]
    public async Task Rebuild_ClearsExistingSegments()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "重建后");
        await Segments.InsertManyAsync(
        [
            new TrainingDatasetItemSegmentRecord
            {
                DatasetId = datasetId,
                ItemId = ObjectId.GenerateNewId(),
                AudioKey = "stale",
                SegmentIndex = 0,
                Text = "陈旧",
                Pinyin = "chen jiu",
                CreatedAt = DateTime.UtcNow,
            },
        ]);
        var service = Create();

        var rebuilt = await service.RebuildAsync(datasetId);

        Assert.Equal(1, rebuilt);
        var segments = await Segments.ListByDatasetAsync(datasetId);
        Assert.Single(segments);
        Assert.Equal(item.Id, segments[0].ItemId);
        Assert.DoesNotContain(segments, s => s.AudioKey == "stale");
    }

    [Fact]
    public async Task SyncItem_ReplacesOldSegments()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "旧文本，旧句子");
        var service = Create();
        await service.SyncItemAsync(item);
        Assert.Equal(2, (await Segments.ListByDatasetAsync(datasetId)).Count);

        item.Text = "新文本";
        await service.SyncItemAsync(item);

        var segments = await Segments.ListByDatasetAsync(datasetId);
        Assert.Single(segments);
        Assert.Equal("新文本", segments[0].Text);
    }

    [Fact]
    public async Task SyncItem_EmptyText_RemovesSegments()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "有文本");
        var service = Create();
        await service.SyncItemAsync(item);
        Assert.Single(await Segments.ListByDatasetAsync(datasetId));

        item.Text = "   ";
        await service.SyncItemAsync(item);

        Assert.Empty(await Segments.ListByDatasetAsync(datasetId));
    }

    [Fact]
    public async Task DeleteItems_RemovesOnlyTargetItems()
    {
        var datasetId = ObjectId.GenerateNewId();
        var keep = await SeedItemAsync(datasetId, "保留");
        var drop = await SeedItemAsync(datasetId, "删除");
        var service = Create();
        await service.SyncItemAsync(keep);
        await service.SyncItemAsync(drop);

        await service.DeleteItemsAsync(datasetId, [drop.Id]);

        var segments = await Segments.ListByDatasetAsync(datasetId);
        Assert.Single(segments);
        Assert.Equal(keep.Id, segments[0].ItemId);
    }

    [Fact]
    public async Task DeleteDataset_RemovesAllSegments()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "你好");
        var service = Create();
        await service.SyncItemAsync(item);
        Assert.NotEmpty(await Segments.ListByDatasetAsync(datasetId));

        await service.DeleteDatasetAsync(datasetId);

        Assert.Empty(await Segments.ListByDatasetAsync(datasetId));
    }
}
