using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.2：`TrainingDatasetItemRepository` 直测（Testcontainers Mongo）——
/// 无序批量导入去重计数（`Imported`/`Skipped`）、列表过滤与排序、部分字段更新与 `endMs` 置空、
/// 批量删除返回删除数、按数据集级联删除。
/// </summary>
public class TrainingDatasetItemRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetItemRepository Repository => new(fixture.Database);

    private static TrainingDatasetItemRecord Item(
        ObjectId datasetId,
        string audioKey,
        string audioType = "transcript",
        string source = "task-1",
        long startMs = 0,
        long? endMs = 30000,
        string text = "句子",
        string audioUri = "b/live/100/recordings/a.flv",
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = audioType,
            Source = source,
            AudioUri = audioUri,
            AudioKey = audioKey,
            StartMs = startMs,
            EndMs = endMs,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task InsertMany_FreshBatch_ImportsAllAndSkipsNothing()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        var items = Enumerable.Range(0, 5)
            .Select(i => Item(datasetId, $"transcript:task-1:{i * 1000}", startMs: i * 1000))
            .ToList();

        var (imported, skipped) = await repository.InsertManyIgnoreDuplicatesAsync(items);

        Assert.Equal(5, imported);
        Assert.Equal(0, skipped);
        Assert.Equal(5, (await repository.ListAsync(datasetId)).Count);
    }

    [Fact]
    public async Task InsertMany_ReimportSameBatch_AllSkipped()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        var items = Enumerable.Range(0, 3)
            .Select(i => Item(datasetId, $"transcript:task-1:{i * 1000}", startMs: i * 1000))
            .ToList();

        await repository.InsertManyIgnoreDuplicatesAsync(items);
        var (imported, skipped) = await repository.InsertManyIgnoreDuplicatesAsync(items);

        Assert.Equal(0, imported);
        Assert.Equal(3, skipped);
        Assert.Equal(3, (await repository.ListAsync(datasetId)).Count);
    }

    [Fact]
    public async Task InsertMany_MixedBatch_CountsImportedAndSkipped()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        await repository.InsertManyIgnoreDuplicatesAsync(new[]
        {
            Item(datasetId, "transcript:task-1:0", startMs: 0),
            Item(datasetId, "transcript:task-1:1000", startMs: 1000),
        });

        // 混合批次：2 条重复（按 AudioKey 撞唯一索引）+ 3 条全新
        var batch = new List<TrainingDatasetItemRecord>
        {
            Item(datasetId, "transcript:task-1:0", startMs: 0),
            Item(datasetId, "transcript:task-1:500", startMs: 500),
            Item(datasetId, "transcript:task-1:1000", startMs: 1000),
            Item(datasetId, "transcript:task-1:1500", startMs: 1500),
            Item(datasetId, "transcript:task-1:2000", startMs: 2000),
        };

        var (imported, skipped) = await repository.InsertManyIgnoreDuplicatesAsync(batch);

        // MongoBulkWriteException 口径：imported = Result.InsertedCount，skipped = 提交数 - imported
        Assert.Equal(3, imported);
        Assert.Equal(2, skipped);
        Assert.Equal(batch.Count, imported + skipped);
        Assert.Equal(5, (await repository.ListAsync(datasetId)).Count);
    }

    [Fact]
    public async Task InsertMany_EmptyBatch_ReturnsZeroZero()
    {
        var repository = Repository;

        var (imported, skipped) = await repository.InsertManyIgnoreDuplicatesAsync([]);

        Assert.Equal(0, imported);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public async Task InsertMany_SameAudioKeyAcrossDatasets_BothAllowed()
    {
        // 唯一索引口径为 (DatasetId, AudioKey)：跨数据集同 key 不冲突
        var repository = Repository;
        var first = ObjectId.GenerateNewId();
        var second = ObjectId.GenerateNewId();

        await repository.InsertManyIgnoreDuplicatesAsync([Item(first, "transcript:task-1:0")]);
        var (imported, skipped) = await repository.InsertManyIgnoreDuplicatesAsync(
            [Item(second, "transcript:task-1:0")]);

        Assert.Equal(1, imported);
        Assert.Equal(0, skipped);
        Assert.Single(await repository.ListAsync(first));
        Assert.Single(await repository.ListAsync(second));
    }

    [Fact]
    public async Task List_FiltersByAudioTypeAndSource_OrdersByCreatedAtThenAudioKey()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        var otherDatasetId = ObjectId.GenerateNewId();
        var baseTime = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);

        await repository.InsertManyIgnoreDuplicatesAsync(
        [
            Item(datasetId, "transcript:task-1:2000", source: "task-1", createdAt: baseTime.AddMinutes(1)),
            Item(datasetId, "transcript:task-1:1000", source: "task-1", createdAt: baseTime.AddMinutes(1)),
            Item(datasetId, "transcript:task-1:0", source: "task-1", createdAt: baseTime),
            Item(datasetId, "recording:abc:100", audioType: "recording", source: "abc",
                audioUri: "b/live/100/recordings/abc.flv", createdAt: baseTime.AddMinutes(2)),
            Item(datasetId, "transcript:task-2:0", source: "task-2", createdAt: baseTime.AddMinutes(3)),
            Item(otherDatasetId, "transcript:task-1:0", source: "task-1", createdAt: baseTime),
        ]);

        var all = await repository.ListAsync(datasetId);
        Assert.Equal(5, all.Count);
        // CreatedAt 升序、同 CreatedAt 内 AudioKey 升序
        Assert.Equal(
            new[] { "transcript:task-1:0", "transcript:task-1:1000", "transcript:task-1:2000",
                "recording:abc:100", "transcript:task-2:0" },
            all.Select(i => i.AudioKey).ToArray());
        Assert.All(all, i => Assert.Equal(datasetId, i.DatasetId));

        var byType = await repository.ListAsync(datasetId, audioType: "transcript");
        Assert.Equal(4, byType.Count);
        Assert.All(byType, i => Assert.Equal("transcript", i.AudioType));

        var bySource = await repository.ListAsync(datasetId, source: "task-2");
        var single = Assert.Single(bySource);
        Assert.Equal("transcript:task-2:0", single.AudioKey);

        var byBoth = await repository.ListAsync(datasetId, audioType: "transcript", source: "task-1");
        Assert.Equal(3, byBoth.Count);

        Assert.Empty(await repository.ListAsync(datasetId, audioType: "missing"));
        Assert.Empty(await repository.ListAsync(ObjectId.GenerateNewId()));
    }

    [Fact]
    public async Task Update_PartialFields_UpdatesOnlyProvidedFields()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        var (imported, _) = await repository.InsertManyIgnoreDuplicatesAsync(
            [Item(datasetId, "transcript:task-1:0", startMs: 1000, endMs: 4000, text: "原句")]);
        Assert.Equal(1, imported);
        var before = (await repository.ListAsync(datasetId)).Single();

        var update = Builders<TrainingDatasetItemRecord>.Update.Set(i => i.Text, "改写后的句子");
        var updated = await repository.UpdateAsync(datasetId, before.Id, update);

        Assert.NotNull(updated);
        Assert.Equal("改写后的句子", updated!.Text);
        // 未提交的字段保持原值
        Assert.Equal(before.AudioUri, updated.AudioUri);
        Assert.Equal(before.AudioKey, updated.AudioKey);
        Assert.Equal(1000, updated.StartMs);
        Assert.Equal(4000, updated.EndMs);
        Assert.Equal(before.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Update_EndMsToNull_ClearsEndMs()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        await repository.InsertManyIgnoreDuplicatesAsync(
            [Item(datasetId, "transcript:task-1:0", startMs: 1000, endMs: 4000)]);
        var item = (await repository.ListAsync(datasetId)).Single();

        var updated = await repository.UpdateAsync(
            datasetId, item.Id, Builders<TrainingDatasetItemRecord>.Update.Set(i => i.EndMs, (long?)null));

        Assert.NotNull(updated);
        Assert.Null(updated!.EndMs);
        Assert.Equal(1000, updated.StartMs);
    }

    [Fact]
    public async Task Update_UnknownIds_ReturnsNullAndWritesNothing()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        await repository.InsertManyIgnoreDuplicatesAsync([Item(datasetId, "transcript:task-1:0", text: "原样")]);
        var item = (await repository.ListAsync(datasetId)).Single();
        var update = Builders<TrainingDatasetItemRecord>.Update.Set(i => i.Text, "不该出现");

        Assert.Null(await repository.UpdateAsync(ObjectId.GenerateNewId(), item.Id, update));
        Assert.Null(await repository.UpdateAsync(datasetId, ObjectId.GenerateNewId(), update));
        Assert.Equal("原样", (await repository.ListAsync(datasetId)).Single().Text);
    }

    [Fact]
    public async Task DeleteMany_PartialMissingIds_ReturnsDeletedCount()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        await repository.InsertManyIgnoreDuplicatesAsync(
        [
            Item(datasetId, "transcript:task-1:0"),
            Item(datasetId, "transcript:task-1:1000"),
            Item(datasetId, "transcript:task-1:2000"),
        ]);
        var kept = (await repository.ListAsync(datasetId)).Single(i => i.AudioKey == "transcript:task-1:2000");
        var ids = (await repository.ListAsync(datasetId))
            .Where(i => i.Id != kept.Id)
            .Select(i => i.Id)
            .Append(ObjectId.GenerateNewId())
            .ToList();

        var deleted = await repository.DeleteManyAsync(datasetId, ids);

        Assert.Equal(2, deleted);
        var rest = await repository.ListAsync(datasetId);
        var survivor = Assert.Single(rest);
        Assert.Equal(kept.Id, survivor.Id);
    }

    [Fact]
    public async Task DeleteMany_UnknownIds_ReturnsZero()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        await repository.InsertManyIgnoreDuplicatesAsync([Item(datasetId, "transcript:task-1:0")]);

        var deleted = await repository.DeleteManyAsync(datasetId, [ObjectId.GenerateNewId()]);

        Assert.Equal(0, deleted);
        Assert.Single(await repository.ListAsync(datasetId));
    }

    [Fact]
    public async Task DeleteByDataset_Cascades_ClearsOnlyThatDataset()
    {
        var repository = Repository;
        var datasetId = ObjectId.GenerateNewId();
        var otherDatasetId = ObjectId.GenerateNewId();
        await repository.InsertManyIgnoreDuplicatesAsync(
        [
            Item(datasetId, "transcript:task-1:0"),
            Item(datasetId, "transcript:task-1:1000"),
            Item(datasetId, "transcript:task-1:2000"),
        ]);
        await repository.InsertManyIgnoreDuplicatesAsync([Item(otherDatasetId, "transcript:task-1:0")]);

        var deleted = await repository.DeleteByDatasetAsync(datasetId);

        Assert.Equal(3, deleted);
        Assert.Empty(await repository.ListAsync(datasetId));
        Assert.Single(await repository.ListAsync(otherDatasetId));

        // 幂等：再次删除返回 0
        Assert.Equal(0, await repository.DeleteByDatasetAsync(datasetId));
    }
}
