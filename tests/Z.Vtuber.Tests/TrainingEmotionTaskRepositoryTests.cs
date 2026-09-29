using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.3：`TrainingEmotionTaskRepository` 直测（Testcontainers Mongo）——落库快照字段、
/// `HasActiveAsync`（PENDING/RUNNING 活跃）、`ListClaimableAsync`（PENDING/RUNNING、CreatedAt 升序、batch 截断）、
/// `ListAsync` 过滤 + limit + CreatedAt 降序、`CountByStatusAsync` 分组、`UpdateAsync` 状态迁移；
/// 另覆盖条目仓储 `SetEmotionAsync`（写 <c>Extensions.emotion</c> 且不动其他键）。
/// </summary>
public class TrainingEmotionTaskRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingEmotionTaskRepository Tasks => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private static TrainingEmotionTaskRecord TaskRecord(
        ObjectId datasetId, ObjectId itemId, string status = TrainingEmotionTaskStatus.Pending,
        DateTime? createdAt = null, int attempts = 0)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return new TrainingEmotionTaskRecord
        {
            DatasetId = datasetId,
            ItemId = itemId,
            AudioKey = $"transcript:t:{itemId}",
            ModelId = ObjectId.GenerateNewId(),
            ModelSlug = "openai-prod/gpt-4o",
            Status = status,
            Attempts = attempts,
            CreatedAt = now,
        };
    }

    private static TrainingDatasetItemRecord Item(
        ObjectId datasetId, string text = "台词", BsonDocument? extensions = null)
    {
        var now = DateTime.UtcNow;
        return new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "t",
            AudioUri = "b/live/1/recordings/a.flv",
            AudioKey = $"transcript:t:{Guid.NewGuid():N}",
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            Extensions = extensions,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Insert_PersistsSnapshotFields()
    {
        var repository = Tasks;
        var datasetId = ObjectId.GenerateNewId();
        var itemId = ObjectId.GenerateNewId();

        var inserted = await repository.InsertAsync(TaskRecord(datasetId, itemId));

        var stored = Assert.Single(await repository.ListAsync(datasetId, null, 100));
        Assert.Equal(inserted.Id, stored.Id);
        Assert.Equal(datasetId, stored.DatasetId);
        Assert.Equal(itemId, stored.ItemId);
        Assert.Equal($"transcript:t:{itemId}", stored.AudioKey);
        Assert.Equal("openai-prod/gpt-4o", stored.ModelSlug);
        Assert.Equal(TrainingEmotionTaskStatus.Pending, stored.Status);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.Error);
        Assert.Null(stored.Emotion);
        Assert.True(stored.CreatedAt != default);
    }

    [Theory]
    [InlineData(TrainingEmotionTaskStatus.Pending, true)]
    [InlineData(TrainingEmotionTaskStatus.Running, true)]
    [InlineData(TrainingEmotionTaskStatus.Done, false)]
    [InlineData(TrainingEmotionTaskStatus.Failed, false)]
    public async Task HasActive_OnlyPendingAndRunning(string status, bool expected)
    {
        var repository = Tasks;
        var itemId = ObjectId.GenerateNewId();
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), itemId, status));

        Assert.Equal(expected, await repository.HasActiveAsync(itemId));
    }

    [Fact]
    public async Task ListClaimable_ReturnsPendingAndRunning_AscendingAndTruncated()
    {
        var repository = Tasks;
        var baseTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Done, baseTime));
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Failed, baseTime.AddMinutes(1)));
        var running = await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Running, baseTime.AddMinutes(2)));
        var pending = await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Pending, baseTime.AddMinutes(3)));

        var claimable = await repository.ListClaimableAsync(10);

        // 仅 PENDING/RUNNING，且 CreatedAt 升序
        Assert.Equal(new[] { running.Id, pending.Id }, claimable.Select(t => t.Id).ToArray());

        // batch 截断
        var truncated = await repository.ListClaimableAsync(1);
        Assert.Single(truncated);
        Assert.Equal(running.Id, truncated[0].Id);
    }

    [Fact]
    public async Task List_FiltersByDatasetAndStatus_DescendingWithLimit()
    {
        var repository = Tasks;
        var datasetA = ObjectId.GenerateNewId();
        var datasetB = ObjectId.GenerateNewId();
        var baseTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Done, baseTime.AddMinutes(1)));
        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Done, baseTime.AddMinutes(3)));
        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Failed, baseTime.AddMinutes(2)));
        await repository.InsertAsync(TaskRecord(datasetB, ObjectId.GenerateNewId(),
            TrainingEmotionTaskStatus.Done, baseTime.AddMinutes(5)));

        // datasetId 过滤 + CreatedAt 降序
        var all = await repository.ListAsync(datasetA, null, 100);
        Assert.Equal(3, all.Count);
        Assert.True(all[0].CreatedAt > all[1].CreatedAt);
        Assert.True(all[1].CreatedAt > all[2].CreatedAt);

        // status 过滤（存储口径大写）
        var done = await repository.ListAsync(datasetA, TrainingEmotionTaskStatus.Done, 100);
        Assert.Equal(2, done.Count);

        // limit 截断
        Assert.Single(await repository.ListAsync(datasetA, null, 1));
    }

    [Fact]
    public async Task CountByStatus_GroupsAndScopesToDataset()
    {
        var repository = Tasks;
        var datasetA = ObjectId.GenerateNewId();
        var datasetB = ObjectId.GenerateNewId();

        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Pending));
        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Pending));
        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Running));
        await repository.InsertAsync(TaskRecord(datasetA, ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Done));
        await repository.InsertAsync(TaskRecord(datasetB, ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Failed));

        var counts = await repository.CountByStatusAsync(datasetA);

        Assert.Equal(2, counts[TrainingEmotionTaskStatus.Pending]);
        Assert.Equal(1, counts[TrainingEmotionTaskStatus.Running]);
        Assert.Equal(1, counts[TrainingEmotionTaskStatus.Done]);
        Assert.False(counts.ContainsKey(TrainingEmotionTaskStatus.Failed));

        var all = await repository.CountByStatusAsync(null);
        Assert.Equal(1, all[TrainingEmotionTaskStatus.Failed]);
    }

    [Fact]
    public async Task TryInsertActive_RejectsSecondActiveForSameItem()
    {
        var repository = Tasks;
        var itemId = ObjectId.GenerateNewId();

        var first = await repository.TryInsertActiveAsync(TaskRecord(ObjectId.GenerateNewId(), itemId));
        Assert.NotNull(first);

        // 同条目第二条活跃任务命中部分唯一索引，返回 null 计 skipped
        var second = await repository.TryInsertActiveAsync(TaskRecord(ObjectId.GenerateNewId(), itemId));
        Assert.Null(second);

        // 终态（FAILED）不受活跃唯一索引约束，可再建
        await repository.UpdateAsync(first!.Id, Builders<TrainingEmotionTaskRecord>.Update
            .Set(t => t.Status, TrainingEmotionTaskStatus.Failed));
        var afterTerminal = await repository.TryInsertActiveAsync(TaskRecord(ObjectId.GenerateNewId(), itemId));
        Assert.NotNull(afterTerminal);
    }

    [Fact]
    public async Task ConcurrentTryInsertActive_OnlyOneSucceeds()
    {
        var repository = Tasks;
        var itemId = ObjectId.GenerateNewId();

        // 并发重复提交：唯一索引兜底，仅一条落库
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => repository.TryInsertActiveAsync(
                TaskRecord(ObjectId.GenerateNewId(), itemId)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r is not null);
        Assert.Single(await repository.ListAsync(null, null, 100));
    }

    [Fact]
    public async Task UpdateRunning_FencesOnStatusAndAttempts()
    {
        var repository = Tasks;
        var task = await repository.InsertAsync(TaskRecord(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Running, attempts: 2));

        // 条件匹配（RUNNING + attempts=2）→ 命中
        var matched = await repository.UpdateRunningAsync(task.Id, 2,
            Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, TrainingEmotionTaskStatus.Done));
        Assert.NotNull(matched);
        Assert.Equal(TrainingEmotionTaskStatus.Done, matched!.Status);

        // 已是 DONE：不再命中
        Assert.Null(await repository.UpdateRunningAsync(task.Id, 2,
            Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, TrainingEmotionTaskStatus.Failed)));

        // 新建一条 RUNNING attempts=0，用陈旧 attempts=1 更新 → 不命中（被接管语义）
        var running = await repository.InsertAsync(TaskRecord(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Running));
        Assert.Null(await repository.UpdateRunningAsync(running.Id, 1,
            Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, TrainingEmotionTaskStatus.Done)));
        Assert.Equal(TrainingEmotionTaskStatus.Running, (await repository.GetByIdAsync(running.Id))!.Status);
    }

    [Fact]
    public async Task RetryFailed_OnlyTransitionsFromFailed()
    {
        var repository = Tasks;

        var failed = await repository.InsertAsync(TaskRecord(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Failed));
        var retried = await repository.RetryFailedAsync(failed.Id,
            Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, TrainingEmotionTaskStatus.Pending));
        Assert.NotNull(retried);
        Assert.Equal(TrainingEmotionTaskStatus.Pending, retried!.Status);

        // 已是 PENDING：再次原子重试不命中（避免把 RUNNING/DONE 重置）
        Assert.Null(await repository.RetryFailedAsync(failed.Id,
            Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, TrainingEmotionTaskStatus.Pending)));

        var running = await repository.InsertAsync(TaskRecord(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), TrainingEmotionTaskStatus.Running));
        Assert.Null(await repository.RetryFailedAsync(running.Id,
            Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, TrainingEmotionTaskStatus.Pending)));
    }

    [Fact]
    public async Task Update_StatusTransition_ReturnsUpdatedRecord()
    {
        var repository = Tasks;
        var id = (await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId()))).Id;
        var startedAt = DateTime.UtcNow;

        var updated = await repository.UpdateAsync(id,
            Builders<TrainingEmotionTaskRecord>.Update
                .Set(t => t.Status, TrainingEmotionTaskStatus.Running)
                .Set(t => t.StartedAt, startedAt)
                .Set(t => t.Attempts, 1));

        Assert.NotNull(updated);
        Assert.Equal(TrainingEmotionTaskStatus.Running, updated!.Status);
        Assert.Equal(1, updated.Attempts);
        Assert.NotNull(updated.StartedAt);

        Assert.Null(await repository.UpdateAsync(
            ObjectId.GenerateNewId(), Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Status, "X")));
    }

    [Fact]
    public async Task SetEmotion_WritesExtensionKey_WhenExtensionsNull()
    {
        var items = Items;
        var datasetId = ObjectId.GenerateNewId();
        var item = await items.GetByIdAsync(datasetId, (await InsertItem(items, datasetId)));

        var emotion = new EmotionResult(0.6, "描述", 0.05, 0, 0.7, 0.05, 0, 0.8, 0.1, 0.4);
        var updated = await items.SetEmotionAsync(item!.DatasetId, item.Id, TrainingMapping.ToEmotionDocument(emotion));

        Assert.NotNull(updated);
        Assert.NotNull(updated!.Extensions);
        Assert.True(updated.Extensions!.Contains("emotion"));
        // 返工 #8：除 Extensions.emotion 外仅刷新 UpdatedAt（与既有编辑口径一致，锁定该行为）
        Assert.True(updated.UpdatedAt >= item.UpdatedAt);
        var mapped = TrainingMapping.FromEmotionDocument(updated.Extensions["emotion"].AsBsonDocument);
        Assert.NotNull(mapped);
        Assert.Equal("描述", mapped!.EmotionDescription);
        Assert.Equal(0.7, mapped.Sad);
    }

    [Fact]
    public async Task SetEmotion_PreservesSiblingExtensionKeys()
    {
        var items = Items;
        var datasetId = ObjectId.GenerateNewId();
        var extensions = new BsonDocument("asr", new BsonDocument("v", 2));
        await InsertItem(items, datasetId, extensions: extensions);
        var item = (await items.ListAsync(datasetId)).Single();

        var emotion = new EmotionResult(0.5, "d", 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1);
        var updated = await items.SetEmotionAsync(datasetId, item.Id, TrainingMapping.ToEmotionDocument(emotion));

        Assert.NotNull(updated);
        Assert.True(updated!.Extensions!.Contains("asr"));
        Assert.Equal(2, updated.Extensions["asr"].AsBsonDocument["v"].AsInt32);
        Assert.True(updated.Extensions.Contains("emotion"));
    }

    [Fact]
    public async Task SetEmotion_UnknownItem_ReturnsNull()
    {
        var items = Items;
        var emotion = new EmotionResult(0.5, "d", 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Null(await items.SetEmotionAsync(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), TrainingMapping.ToEmotionDocument(emotion)));
    }

    private static async Task<ObjectId> InsertItem(
        TrainingDatasetItemRepository items, ObjectId datasetId, BsonDocument? extensions = null)
    {
        var item = Item(datasetId, extensions: extensions);
        await items.InsertManyIgnoreDuplicatesAsync([item]);
        return item.Id;
    }
}
