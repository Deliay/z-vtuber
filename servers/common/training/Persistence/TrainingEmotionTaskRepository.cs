using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// 情绪识别任务仓储：<c>training_emotion_tasks</c>。
/// 索引 <c>(Status, CreatedAt)</c>（扫描领取）、<c>(DatasetId, CreatedAt)</c>（队列查询）、<c>ItemId</c>（重复提交判定）。
/// </summary>
public sealed class TrainingEmotionTaskRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingEmotionTaskRecord> _collection =
        database.GetCollection<TrainingEmotionTaskRecord>(TrainingCollectionNames.TrainingEmotionTasks);

    /// <summary>按 datasetId/status 过滤，CreatedAt 降序，最多 <paramref name="limit"/> 条。</summary>
    public async Task<List<TrainingEmotionTaskRecord>> ListAsync(
        ObjectId? datasetId, string? status, int limit, CancellationToken ct = default)
    {
        var filter = Builders<TrainingEmotionTaskRecord>.Filter.Empty;
        if (datasetId.HasValue)
        {
            filter &= Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.DatasetId, datasetId.Value);
        }

        if (!string.IsNullOrEmpty(status))
        {
            filter &= Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.Status, status);
        }

        return await _collection.Find(filter)
            .SortByDescending(t => t.CreatedAt)
            .Limit(Math.Max(1, limit))
            .ToListAsync(ct);
    }

    /// <summary>各状态分组计数（key 为存储口径大写状态）。</summary>
    public async Task<Dictionary<string, long>> CountByStatusAsync(
        ObjectId? datasetId, CancellationToken ct = default)
    {
        var filter = Builders<TrainingEmotionTaskRecord>.Filter.Empty;
        if (datasetId.HasValue)
        {
            filter &= Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.DatasetId, datasetId.Value);
        }

        var grouped = await _collection.Aggregate()
            .Match(filter)
            .Group(
                t => t.Status,
                g => new { Status = g.Key, Count = g.LongCount() })
            .ToListAsync(ct);

        return grouped.ToDictionary(g => g.Status, g => g.Count);
    }

    public async Task<TrainingEmotionTaskRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(t => t.Id == id).FirstOrDefaultAsync(ct);

    public async Task<TrainingEmotionTaskRecord> InsertAsync(
        TrainingEmotionTaskRecord record, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    /// <summary>
    /// 原子幂等插入：条目已有活跃（PENDING/RUNNING）任务时命中 <c>ItemId_1_Active</c> 部分唯一索引，
    /// 捕获唯一键冲突返回 null（调用方计 skipped）。并发重复提交（双击/双副本）也只可能建一条。
    /// </summary>
    public async Task<TrainingEmotionTaskRecord?> TryInsertActiveAsync(
        TrainingEmotionTaskRecord record, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(record, cancellationToken: ct);
            return record;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    /// <summary>条目是否已有活跃（PENDING/RUNNING）任务——重复提交幂等的判定依据。</summary>
    public async Task<bool> HasActiveAsync(ObjectId itemId, CancellationToken ct = default)
    {
        var filter = Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.ItemId, itemId)
            & Builders<TrainingEmotionTaskRecord>.Filter.In(
                t => t.Status,
                new[] { TrainingEmotionTaskStatus.Pending, TrainingEmotionTaskStatus.Running });
        return await _collection.Find(filter).AnyAsync(ct);
    }

    /// <summary>
    /// 扫描领取：PENDING 或 RUNNING（RUNNING 是否可接管由扫描器按锁存在性判定），
    /// CreatedAt 升序（入队顺序），最多 <paramref name="batchSize"/> 条。
    /// </summary>
    public async Task<List<TrainingEmotionTaskRecord>> ListClaimableAsync(
        int batchSize, CancellationToken ct = default)
    {
        var filter = Builders<TrainingEmotionTaskRecord>.Filter.In(
            t => t.Status,
            new[] { TrainingEmotionTaskStatus.Pending, TrainingEmotionTaskStatus.Running });

        return await _collection.Find(filter)
            .SortBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Limit(Math.Max(1, batchSize))
            .ToListAsync(ct);
    }

    /// <summary>按更新定义更新；未命中返回 null。</summary>
    public async Task<TrainingEmotionTaskRecord?> UpdateAsync(
        ObjectId id, UpdateDefinition<TrainingEmotionTaskRecord> update, CancellationToken ct = default)
    {
        var result = await _collection.UpdateOneAsync(t => t.Id == id, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await _collection.Find(t => t.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// fencing 写库：仅当任务仍为「本节点领取的 RUNNING + 同一 <paramref name="attempts"/>」时才应用更新，
    /// 否则返回 null（锁已易主/任务被接管，调用方必须放弃写入）。把 fencing 落到写操作本身，闭合
    /// 「校验锁 → 写库」之间的 TOCTOU 窗口。
    /// </summary>
    public async Task<TrainingEmotionTaskRecord?> UpdateRunningAsync(
        ObjectId id, int attempts, UpdateDefinition<TrainingEmotionTaskRecord> update,
        CancellationToken ct = default)
    {
        var filter = Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.Id, id)
            & Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.Status, TrainingEmotionTaskStatus.Running)
            & Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.Attempts, attempts);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await _collection.Find(t => t.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 原子重试：仅当任务当前为 FAILED 时才迁移（filter 含 <c>Status==FAILED</c>），并发双击/与扫描器
    /// 交错时只有一个命中，未命中返回 null。若同条目已有其他活跃任务（部分唯一索引冲突）则抛出
    /// <see cref="MongoWriteException"/>（DuplicateKey），由调用方映射为冲突。
    /// </summary>
    public async Task<TrainingEmotionTaskRecord?> RetryFailedAsync(
        ObjectId id, UpdateDefinition<TrainingEmotionTaskRecord> update, CancellationToken ct = default)
    {
        var filter = Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.Id, id)
            & Builders<TrainingEmotionTaskRecord>.Filter.Eq(t => t.Status, TrainingEmotionTaskStatus.Failed);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await _collection.Find(t => t.Id == id).FirstOrDefaultAsync(ct);
    }
}
