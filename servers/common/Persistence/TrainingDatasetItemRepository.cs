using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// 训练数据集条目仓储：<c>training_dataset_items</c>，唯一索引 <c>(DatasetId, AudioKey)</c>——
/// 导入去重与标识唯一性的实现基础。
/// </summary>
public sealed class TrainingDatasetItemRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingDatasetItemRecord> _collection =
        database.GetCollection<TrainingDatasetItemRecord>(MongoCollectionNames.TrainingDatasetItems);

    /// <summary>
    /// 列举数据集条目（CreatedAt、AudioKey 升序）；<paramref name="audioType"/>/<paramref name="source"/>
    /// 为空则不过滤。
    /// </summary>
    public async Task<List<TrainingDatasetItemRecord>> ListAsync(
        ObjectId datasetId, string? audioType = null, string? source = null,
        CancellationToken ct = default)
    {
        var filter = Builders<TrainingDatasetItemRecord>.Filter.Eq(i => i.DatasetId, datasetId);
        if (!string.IsNullOrEmpty(audioType))
        {
            filter &= Builders<TrainingDatasetItemRecord>.Filter.Eq(i => i.AudioType, audioType);
        }
        if (!string.IsNullOrEmpty(source))
        {
            filter &= Builders<TrainingDatasetItemRecord>.Filter.Eq(i => i.Source, source);
        }

        return await _collection.Find(filter)
            .SortBy(i => i.CreatedAt)
            .ThenBy(i => i.AudioKey)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 无序批量导入：按 <c>(DatasetId, AudioKey)</c> 唯一索引跳过重复。
    /// 返回 (Imported, Skipped)，Skipped = 提交数 − Imported。
    /// </summary>
    public async Task<(long Imported, long Skipped)> InsertManyIgnoreDuplicatesAsync(
        IEnumerable<TrainingDatasetItemRecord> items, CancellationToken ct = default)
    {
        var submitted = items as IReadOnlyList<TrainingDatasetItemRecord> ?? items.ToList();
        if (submitted.Count == 0)
        {
            return (0, 0);
        }

        try
        {
            await _collection.InsertManyAsync(
                submitted, new InsertManyOptions { IsOrdered = false }, ct);
            return (submitted.Count, 0);
        }
        catch (MongoBulkWriteException<TrainingDatasetItemRecord> ex)
        {
            var imported = ex.Result.InsertedCount;
            return (imported, submitted.Count - imported);
        }
    }

    /// <summary>按 (datasetId, itemId) 应用更新定义；未命中返回 null。</summary>
    public async Task<TrainingDatasetItemRecord?> UpdateAsync(
        ObjectId datasetId, ObjectId itemId,
        UpdateDefinition<TrainingDatasetItemRecord> update, CancellationToken ct = default)
    {
        var matched = await _collection.UpdateOneAsync(
            i => i.DatasetId == datasetId && i.Id == itemId, update, cancellationToken: ct);
        if (matched.MatchedCount == 0)
        {
            return null;
        }

        return await _collection
            .Find(i => i.DatasetId == datasetId && i.Id == itemId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>删除指定条目；不存在的 id 跳过，返回实际删除数。</summary>
    public async Task<long> DeleteManyAsync(
        ObjectId datasetId, IEnumerable<ObjectId> itemIds, CancellationToken ct = default)
    {
        var ids = itemIds.ToList();
        var filter = Builders<TrainingDatasetItemRecord>.Filter.Eq(i => i.DatasetId, datasetId)
            & Builders<TrainingDatasetItemRecord>.Filter.In(i => i.Id, ids);
        var result = await _collection.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }

    /// <summary>级联删除数据集全部条目，返回删除数。</summary>
    public async Task<long> DeleteByDatasetAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        var result = await _collection.DeleteManyAsync(
            i => i.DatasetId == datasetId, ct);
        return result.DeletedCount;
    }
}
