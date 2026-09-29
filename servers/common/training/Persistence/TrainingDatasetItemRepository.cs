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
        database.GetCollection<TrainingDatasetItemRecord>(TrainingCollectionNames.TrainingDatasetItems);

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
    /// 仅当**全部**写失败都是唯一键冲突、且无未处理请求（<c>UnprocessedRequests</c> 为空）才按去重计数；
    /// 其余写失败（集合校验、写关注异常、WriteErrors 为空、中途中断等）一律原样上抛，
    /// 避免把「未落库」误报成「已去重跳过」导致数据静默丢失。
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
            if (ex.WriteErrors.Count > 0
                && ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey)
                && ex.UnprocessedRequests.Count == 0)
            {
                var imported = ex.Result.InsertedCount;
                return (imported, submitted.Count - imported);
            }

            throw;
        }
    }

    /// <summary>按 (datasetId, itemId) 读取单条条目；不存在返回 null（避免为读一条而全量列举数据集）。</summary>
    public async Task<TrainingDatasetItemRecord?> GetByIdAsync(
        ObjectId datasetId, ObjectId itemId, CancellationToken ct = default) =>
        await _collection
            .Find(i => i.DatasetId == datasetId && i.Id == itemId)
            .FirstOrDefaultAsync(ct);

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

    /// <summary>
    /// 把情绪识别结果写入条目 <c>Extensions.emotion</c>（只 <c>$set</c> 该键，不动 <c>Extensions</c> 下其他键，
    /// 兼容未来二次 ASR 等加工结果）；同一数据重复识别以最新结果覆盖。除目标键外仅刷新条目
    /// <c>UpdatedAt</c>（与既有编辑口径一致，属有意行为，测试已锁定）。未命中返回 null。
    /// </summary>
    public async Task<TrainingDatasetItemRecord?> SetEmotionAsync(
        ObjectId datasetId, ObjectId itemId, BsonDocument emotion, CancellationToken ct = default)
    {
        var filter = Builders<TrainingDatasetItemRecord>.Filter.Eq(i => i.DatasetId, datasetId)
            & Builders<TrainingDatasetItemRecord>.Filter.Eq(i => i.Id, itemId);

        // 兜底：Extensions 缺失或为 null 时先初始化为空文档——
        // 否则 $set "Extensions.emotion" 会因父字段为 BSON null 报错（Cannot create field ... in element {Extensions: null}）。
        await _collection.UpdateOneAsync(
            filter & Builders<TrainingDatasetItemRecord>.Filter.Eq("Extensions", BsonNull.Value),
            Builders<TrainingDatasetItemRecord>.Update.Set("Extensions", new BsonDocument()),
            cancellationToken: ct);

        var update = Builders<TrainingDatasetItemRecord>.Update
            .Set("Extensions.emotion", emotion)
            .Set(i => i.UpdatedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }
}
