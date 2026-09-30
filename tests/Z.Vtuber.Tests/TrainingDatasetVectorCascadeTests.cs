using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Tests;

/// <summary>
/// P2-1 回归：<c>items/batch-delete</c> 的向量级联必须限制在本数据集、且只针对 Mongo 实际删除的条目。
/// 传入属于他数据集的条目 id 时，Mongo 侧不删（0 行），Milvus 侧也不得删除他数据集向量。
/// 覆盖控制器级「级联范围收敛」与真 Milvus 端到端「跨数据集不误删」两层。
/// </summary>
public class TrainingDatasetVectorCascadeTests(MongoDbFixture mongo, MilvusFixture milvus)
    : IClassFixture<MongoDbFixture>, IClassFixture<MilvusFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(mongo.Database);

    private TrainingDatasetItemRepository Items => new(mongo.Database);

    private async Task<ObjectId> SeedDatasetAsync(string name)
    {
        var now = DateTime.UtcNow;
        var record = await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = name,
            Type = "audio",
            CreatedAt = now,
            UpdatedAt = now,
        });
        return record.Id;
    }

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(ObjectId datasetId, string audioKey, string text)
    {
        var now = DateTime.UtcNow;
        var record = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task-1",
            AudioUri = "b/live/700001/recordings/a.flv",
            AudioKey = audioKey,
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([record]);
        return record;
    }

    private TrainingDatasetItemsController CreateController(VectorSyncService sync) =>
        new(Datasets, Items, new StubTranscriptClient(),
            NullLogger<TrainingDatasetItemsController>.Instance, sync)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    // ---- 控制器级：级联范围收敛为实际被 Mongo 删除的条目 ----

    [Fact]
    public async Task BatchDelete_CrossDatasetId_OnlyCascadesActuallyDeletedItems()
    {
        var datasetA = await SeedDatasetAsync("A");
        var datasetB = await SeedDatasetAsync("B");
        var itemA = await SeedItemAsync(datasetA, "a-key", "A 的句子");
        var itemB = await SeedItemAsync(datasetB, "b-key", "B 的句子");

        var store = new RecordingVectorStore();
        var sync = new VectorSyncService(Items, store, new FakeEmbeddingClient(), NullLogger<VectorSyncService>.Instance);
        var controller = CreateController(sync);

        // 对 A 提交 batch-delete，混入属于 B 的 item id
        var result = await controller.BatchDelete(
            datasetA.ToString(),
            new TrainingDatasetBatchDeleteRequest([itemA.Id.ToString(), itemB.Id.ToString()]));

        var payload = Assert.IsType<TrainingDatasetBatchDeleteResultDto>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(1, payload.Deleted);

        // 只级联删除 A 实际删掉的条目，绝不触碰 B 的条目
        Assert.Equal([itemA.Id], store.DeletedItems);
        Assert.DoesNotContain(itemB.Id, store.DeletedItems);

        // B 的条目仍在 Mongo
        Assert.Single(await Items.ListAsync(datasetB));
    }

    // ---- 真 Milvus 端到端：跨数据集不误删派生向量 ----

    [Fact]
    public async Task BatchDelete_CrossDatasetItemId_DoesNotDeleteOtherDatasetVectors()
    {
        var store = milvus.CreateStore(dimensions: 3);
        await store.EnsureCollectionsAsync();
        var sync = new VectorSyncService(Items, store, new FakeEmbeddingClient(), NullLogger<VectorSyncService>.Instance);

        var datasetA = await SeedDatasetAsync("A");
        var datasetB = await SeedDatasetAsync("B");
        var itemB = await SeedItemAsync(datasetB, "b-key", "B 的句子");
        await sync.SyncItemAsync(itemB);

        var controller = CreateController(sync);
        var result = await controller.BatchDelete(
            datasetA.ToString(), new TrainingDatasetBatchDeleteRequest([itemB.Id.ToString()]));

        var payload = Assert.IsType<TrainingDatasetBatchDeleteResultDto>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(0, payload.Deleted);
        Assert.Single(await Items.ListAsync(datasetB));

        // B 的派生向量必须仍在（此前 bug：Mongo 删 0 行但 Milvus 按 id 全删，B 静默丢失候选）
        var matches = await store.SearchTextAsync(datasetB, [0.1f, 0.2f, 0.3f], 4);
        Assert.Contains(matches, m => m.ItemId == itemB.Id);
    }

    [Fact]
    public async Task BatchDelete_DeletesOwnItemVectors_AndLeavesOtherDatasetVectors()
    {
        var store = milvus.CreateStore(dimensions: 3);
        await store.EnsureCollectionsAsync();
        var sync = new VectorSyncService(Items, store, new FakeEmbeddingClient(), NullLogger<VectorSyncService>.Instance);

        var datasetA = await SeedDatasetAsync("A");
        var datasetB = await SeedDatasetAsync("B");
        var itemA = await SeedItemAsync(datasetA, "a-key", "A 的句子");
        var itemB = await SeedItemAsync(datasetB, "b-key", "B 的句子");
        await sync.SyncItemAsync(itemA);
        await sync.SyncItemAsync(itemB);

        var controller = CreateController(sync);
        await controller.BatchDelete(
            datasetA.ToString(), new TrainingDatasetBatchDeleteRequest([itemA.Id.ToString(), itemB.Id.ToString()]));

        Assert.Empty(await store.SearchTextAsync(datasetA, [0.1f, 0.2f, 0.3f], 4));
        var leftover = await store.SearchTextAsync(datasetB, [0.1f, 0.2f, 0.3f], 4);
        Assert.Contains(leftover, m => m.ItemId == itemB.Id);
    }

    /// <summary>记录被级联删除的条目 id 的向量库替身（其余操作空实现）。</summary>
    private sealed class RecordingVectorStore : IVectorStore
    {
        public List<ObjectId> DeletedItems { get; } = [];

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorMatch>> SearchTextAsync(
            ObjectId datasetId, float[] query, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VectorMatch>>([]);

        public Task<IReadOnlyList<VectorMatch>> SearchEmotionAsync(
            ObjectId datasetId, float[] query, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VectorMatch>>([]);

        public Task UpsertItemVectorsAsync(
            ObjectId datasetId, ObjectId itemId, string audioKey,
            float[]? textEmbedding, float[]? emotionVector, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteItemVectorsAsync(ObjectId datasetId, ObjectId itemId, CancellationToken ct = default)
        {
            DeletedItems.Add(itemId);
            return Task.CompletedTask;
        }

        public Task DeleteDatasetVectorsAsync(ObjectId datasetId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubTranscriptClient : ITranscriptApiClient
    {
        public Task<TranscriptTaskDetail?> GetTaskDetailAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult<TranscriptTaskDetail?>(null);
    }
}
