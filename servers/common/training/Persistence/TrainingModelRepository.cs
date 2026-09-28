using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// 模型仓储：<c>training_models</c>，唯一索引 <c>(ProviderId, Model)</c>——
/// 手动添加/导入撞唯一索引由数据库抛 DuplicateKey；批量导入按索引冲突计 skipped。
/// </summary>
public sealed class TrainingModelRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingModelRecord> _collection =
        database.GetCollection<TrainingModelRecord>(TrainingCollectionNames.TrainingModels);

    /// <summary>列举模型，CreatedAt 升序；<paramref name="enabled"/> 非空则按启用状态过滤。</summary>
    public async Task<List<TrainingModelRecord>> ListAsync(
        bool? enabled = null, CancellationToken ct = default)
    {
        var filter = Builders<TrainingModelRecord>.Filter.Empty;
        if (enabled.HasValue)
        {
            filter &= Builders<TrainingModelRecord>.Filter.Eq(m => m.Enabled, enabled.Value);
        }

        return await _collection.Find(filter)
            .SortBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>按 id 读取；不存在返回 null。</summary>
    public async Task<TrainingModelRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct);

    /// <summary>插入模型（落库前 Trim）；(ProviderId, Model) 重复抛 DuplicateKey。</summary>
    public async Task<TrainingModelRecord> InsertAsync(
        TrainingModelRecord record, CancellationToken ct = default)
    {
        record.Model = record.Model.Trim();
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    /// <summary>
    /// 无序批量导入：按 <c>(ProviderId, Model)</c> 唯一索引跳过重复，返回 (Imported, Skipped)。
    /// 仅当全部写失败都是唯一键冲突才按去重计数；其余写失败原样上抛，避免把「未落库」误报为「已跳过」。
    /// </summary>
    public async Task<(long Imported, long Skipped)> InsertManyIgnoreDuplicatesAsync(
        IEnumerable<TrainingModelRecord> items, CancellationToken ct = default)
    {
        var submitted = items as IReadOnlyList<TrainingModelRecord> ?? items.ToList();
        foreach (var item in submitted)
        {
            item.Model = item.Model.Trim();
        }

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
        catch (MongoBulkWriteException<TrainingModelRecord> ex)
        {
            if (ex.WriteErrors.Count > 0
                && ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
            {
                var imported = ex.Result.InsertedCount;
                return (imported, submitted.Count - imported);
            }

            throw;
        }
    }

    /// <summary>按更新定义更新；未命中返回 null。改名撞唯一索引抛 DuplicateKey。</summary>
    public async Task<TrainingModelRecord?> UpdateAsync(
        ObjectId id, UpdateDefinition<TrainingModelRecord> update, CancellationToken ct = default)
    {
        var result = await _collection.UpdateOneAsync(m => m.Id == id, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await _collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>删除模型；未命中返回 false。</summary>
    public async Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        var result = await _collection.DeleteOneAsync(m => m.Id == id, cancellationToken: ct);
        return result.DeletedCount > 0;
    }

    /// <summary>级联删除某 Provider 下全部模型，返回删除数。</summary>
    public async Task<long> DeleteByProviderAsync(ObjectId providerId, CancellationToken ct = default)
    {
        var result = await _collection.DeleteManyAsync(m => m.ProviderId == providerId, ct);
        return result.DeletedCount;
    }
}
