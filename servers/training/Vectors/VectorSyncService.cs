using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Vectors;

/// <summary>
/// MongoDB → Milvus 向量同步：文本向量由本地 embedding 对 <c>Text</c> 现算，情感向量取
/// <c>Extensions.emotion</c> 八维。供 <c>vectors/rebuild</c> 全量重建与导入/编辑/情绪写入后增量同步。
/// <see cref="RebuildAsync"/> 失败原样上抛（映射 502）；增量 <see cref="SyncItemAsync"/>/
/// <see cref="DeleteDatasetAsync"/> 为 best-effort（失败仅 Warn，不阻断主流程，待 rebuild 兜底）。
/// </summary>
public sealed class VectorSyncService(
    TrainingDatasetItemRepository items,
    IVectorStore store,
    IEmbeddingClient embedding,
    ILogger<VectorSyncService> logger)
{
    /// <summary>
    /// 全量重建数据集向量：先清空该数据集在两个集合中的旧向量，再逐条 upsert；返回写入条数
    /// （无文本且无情绪的条目跳过不计）。失败抛 <see cref="EmbeddingUnavailableException"/>/
    /// <see cref="VectorStoreUnavailableException"/>。
    /// </summary>
    public async Task<long> RebuildAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        await store.EnsureCollectionsAsync(ct);
        var records = await items.ListAsync(datasetId, ct: ct);
        await store.DeleteDatasetVectorsAsync(datasetId, ct);

        var updated = 0L;
        foreach (var record in records)
        {
            var (text, emotion) = await BuildVectorsAsync(record, ct);
            if (text is null && emotion is null)
            {
                continue;
            }

            await store.UpsertItemVectorsAsync(datasetId, record.Id, record.AudioKey, text, emotion, ct);
            updated++;
        }

        logger.LogInformation(
            "Vector rebuild: datasetId={DatasetId} items={Items} updated={Updated}",
            datasetId, records.Count, updated);
        return updated;
    }

    /// <summary>单条增量同步（best-effort，失败仅 Warn 不阻断主流程）。</summary>
    public async Task SyncItemAsync(TrainingDatasetItemRecord record, CancellationToken ct = default)
    {
        try
        {
            var (text, emotion) = await BuildVectorsAsync(record, ct);
            if (text is null && emotion is null)
            {
                return;
            }

            await store.UpsertItemVectorsAsync(record.DatasetId, record.Id, record.AudioKey, text, emotion, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Vector sync failed for datasetId={DatasetId} itemId={ItemId}",
                record.DatasetId, record.Id);
        }
    }

    /// <summary>删除数据集全部向量（best-effort）。</summary>
    public async Task DeleteDatasetAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        try
        {
            await store.DeleteDatasetVectorsAsync(datasetId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vector delete failed for datasetId={DatasetId}", datasetId);
        }
    }

    private async Task<(float[]? Text, float[]? Emotion)> BuildVectorsAsync(
        TrainingDatasetItemRecord record, CancellationToken ct)
    {
        float[]? text = string.IsNullOrWhiteSpace(record.Text)
            ? null
            : await embedding.GetEmbeddingAsync(record.Text, ct);

        var emotion = ReadEmotion(record);
        float[]? emotionVector = emotion is null ? null : Synthesis.EmotionVector.FromEmotion(emotion);
        return (text, emotionVector);
    }

    private static Contracts.EmotionResult? ReadEmotion(TrainingDatasetItemRecord record) =>
        record.Extensions is not null
        && record.Extensions.TryGetValue("emotion", out var raw)
        && raw.IsBsonDocument
            ? TrainingMapping.FromEmotionDocument(raw.AsBsonDocument)
            : null;
}
