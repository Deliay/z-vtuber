using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// 训练数据集候选短句仓储：<c>training_dataset_item_segments</c>。
/// 索引 <c>(DatasetId)</c>（按数据集加载/清空/级联）与 <c>(DatasetId, ItemId)</c>（条目级替换/删除）。
/// 拼音相似度为内存计算，故不建 <c>Pinyin</c> 等值索引。
/// </summary>
public sealed class TrainingDatasetItemSegmentRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingDatasetItemSegmentRecord> _collection =
        database.GetCollection<TrainingDatasetItemSegmentRecord>(
            TrainingCollectionNames.TrainingDatasetItemSegments);

    /// <summary>按数据集加载全部候选短句（ItemId、SegmentIndex 升序，确定性）。</summary>
    public async Task<List<TrainingDatasetItemSegmentRecord>> ListByDatasetAsync(
        ObjectId datasetId, CancellationToken ct = default) =>
        await _collection.Find(s => s.DatasetId == datasetId)
            .SortBy(s => s.ItemId)
            .ThenBy(s => s.SegmentIndex)
            .ToListAsync(ct);

    /// <summary>替换单条目候选短句：先删该条目旧行再插新行（无短句时仅删）。</summary>
    public async Task ReplaceForItemAsync(
        ObjectId datasetId, ObjectId itemId, IEnumerable<TrainingDatasetItemSegmentRecord> segments,
        CancellationToken ct = default)
    {
        await _collection.DeleteManyAsync(
            s => s.DatasetId == datasetId && s.ItemId == itemId, ct);

        var list = segments as IReadOnlyList<TrainingDatasetItemSegmentRecord> ?? segments.ToList();
        if (list.Count > 0)
        {
            await _collection.InsertManyAsync(list, cancellationToken: ct);
        }
    }

    /// <summary>级联删除数据集全部候选短句，返回删除数。</summary>
    public async Task<long> DeleteByDatasetAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        var result = await _collection.DeleteManyAsync(s => s.DatasetId == datasetId, ct);
        return result.DeletedCount;
    }

    /// <summary>批量删除若干条目的候选短句（batch-delete 级联）。</summary>
    public async Task<long> DeleteItemsAsync(
        ObjectId datasetId, IEnumerable<ObjectId> itemIds, CancellationToken ct = default)
    {
        var ids = itemIds.ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var result = await _collection.DeleteManyAsync(
            s => s.DatasetId == datasetId && ids.Contains(s.ItemId), ct);
        return result.DeletedCount;
    }

    /// <summary>批量插入候选短句（rebuild 使用）。</summary>
    public async Task InsertManyAsync(
        IEnumerable<TrainingDatasetItemSegmentRecord> segments, CancellationToken ct = default)
    {
        var list = segments as IReadOnlyList<TrainingDatasetItemSegmentRecord> ?? segments.ToList();
        if (list.Count > 0)
        {
            await _collection.InsertManyAsync(list, cancellationToken: ct);
        }
    }

    /// <summary>统计数据集候选短句数。</summary>
    public async Task<long> CountByDatasetAsync(ObjectId datasetId, CancellationToken ct = default) =>
        await _collection.CountDocumentsAsync(s => s.DatasetId == datasetId, cancellationToken: ct);
}
