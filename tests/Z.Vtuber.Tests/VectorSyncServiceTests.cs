using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Tests;

/// <summary>
/// MongoDB → Milvus 向量同步：rebuild 逐条现算文本向量 + 取情绪八维、无情绪只写文本、
/// 空文本且无情绪跳过；增量失败仅 Warn 不抛；删除 best-effort。
/// </summary>
public class VectorSyncServiceTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private static TrainingDatasetItemRecord Item(
        ObjectId datasetId, string text, EmotionResult? emotion = null, string? audioKey = null)
    {
        var now = DateTime.UtcNow;
        return new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task-1",
            AudioUri = "b/live/1/recordings/a.flv",
            AudioKey = audioKey ?? $"k-{Guid.NewGuid():N}",
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            Extensions = emotion is null
                ? null
                : new BsonDocument { { "emotion", TrainingMapping.ToEmotionDocument(emotion) } },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static EmotionResult Happy() =>
        new(5, "开心", 0.9, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public async Task Rebuild_ComputesTextAndEmotionVectors_AndCountsWrittenItems()
    {
        var datasetId = ObjectId.GenerateNewId();
        await Items.InsertManyIgnoreDuplicatesAsync(
        [
            Item(datasetId, "有情绪", Happy()),
            Item(datasetId, "只有文本"),
            Item(datasetId, ""),
        ]);

        var embedding = new FakeEmbeddingClient();
        var store = new InMemoryVectorStore();
        var service = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);

        var updated = await service.RebuildAsync(datasetId);

        Assert.Equal(2, updated);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(2, store.UpsertCalls);
        Assert.Equal(2, embedding.Calls);
    }

    [Fact]
    public async Task Rebuild_EmbeddingFailure_Propagates()
    {
        var datasetId = ObjectId.GenerateNewId();
        await Items.InsertManyIgnoreDuplicatesAsync([Item(datasetId, "文本")]);

        var embedding = new FakeEmbeddingClient { Throw = new EmbeddingUnavailableException("down") };
        var store = new InMemoryVectorStore();
        var service = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => service.RebuildAsync(datasetId));
    }

    [Fact]
    public async Task SyncItem_StoreFailure_IsSwallowed()
    {
        var datasetId = ObjectId.GenerateNewId();
        var embedding = new FakeEmbeddingClient();
        var store = new InMemoryVectorStore { UpsertThrow = new VectorStoreUnavailableException("down") };
        var service = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);

        // 不抛异常（best-effort）。
        await service.SyncItemAsync(Item(datasetId, "文本"));
    }

    [Fact]
    public async Task SyncItem_EmptyTextNoEmotion_DoesNotCallStore()
    {
        var datasetId = ObjectId.GenerateNewId();
        var embedding = new FakeEmbeddingClient();
        var store = new InMemoryVectorStore();
        var service = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);

        await service.SyncItemAsync(Item(datasetId, ""));

        Assert.Equal(0, store.UpsertCalls);
        Assert.Equal(0, embedding.Calls);
    }

    [Fact]
    public async Task DeleteDataset_BestEffort()
    {
        var embedding = new FakeEmbeddingClient();
        var store = new InMemoryVectorStore();
        var service = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);

        await service.DeleteDatasetAsync(ObjectId.GenerateNewId());

        Assert.Equal(1, store.DeleteCalls);
    }
}
