using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// 功能点仓储：<c>training_feature_points</c>，唯一索引 <c>Slug</c>。
/// 登记走按 Slug 的 upsert（<c>SetOnInsert CreatedAt</c>，已存在仅更新 Name/能力要求/UpdatedAt，
/// 保留既有 <c>ModelId</c> 指定）；指定/置空走 <see cref="SetModelAsync"/>。
/// </summary>
public sealed class TrainingFeaturePointRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingFeaturePointRecord> _collection =
        database.GetCollection<TrainingFeaturePointRecord>(TrainingCollectionNames.TrainingFeaturePoints);

    /// <summary>列举功能点，Slug 升序。</summary>
    public async Task<List<TrainingFeaturePointRecord>> ListAsync(CancellationToken ct = default) =>
        await _collection.Find(Builders<TrainingFeaturePointRecord>.Filter.Empty)
            .SortBy(f => f.Slug)
            .ToListAsync(ct);

    /// <summary>按 slug 读取；不存在返回 null。</summary>
    public async Task<TrainingFeaturePointRecord?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        await _collection.Find(f => f.Slug == slug).FirstOrDefaultAsync(ct);

    /// <summary>
    /// 按 slug 登记（upsert）：首次插入用 <paramref name="record"/> 的 CreatedAt/Id/能力要求，
    /// 已存在则仅更新 Name/能力要求/UpdatedAt（<c>ModelId</c> 指定保持不变），返回库中最新记录。
    /// 并发 upsert 撞唯一索引时重试一次（退化为纯更新）。
    /// </summary>
    public async Task<TrainingFeaturePointRecord> RegisterAsync(
        TrainingFeaturePointRecord record, CancellationToken ct = default)
    {
        var filter = Builders<TrainingFeaturePointRecord>.Filter.Eq(f => f.Slug, record.Slug);
        var update = Builders<TrainingFeaturePointRecord>.Update
            .SetOnInsert(f => f.Id, record.Id)
            .SetOnInsert(f => f.CreatedAt, record.CreatedAt)
            .Set(f => f.Name, record.Name)
            .Set(f => f.InputCapabilities, record.InputCapabilities)
            .Set(f => f.OutputCapabilities, record.OutputCapabilities)
            .Set(f => f.UpdatedAt, record.UpdatedAt);

        try
        {
            await _collection.UpdateOneAsync(
                filter, update, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // 并发首次登记撞 Slug 唯一索引：对方已插入，重试一次走纯更新路径。
            await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        }

        return (await GetBySlugAsync(record.Slug, ct))!;
    }

    /// <summary>指定/置空模型（<paramref name="modelId"/> 为 null 即取消指定）；未命中返回 null。</summary>
    public async Task<TrainingFeaturePointRecord?> SetModelAsync(
        string slug, ObjectId? modelId, DateTime now, CancellationToken ct = default)
    {
        var update = Builders<TrainingFeaturePointRecord>.Update
            .Set(f => f.ModelId, modelId)
            .Set(f => f.UpdatedAt, now);
        var result = await _collection.UpdateOneAsync(f => f.Slug == slug, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await GetBySlugAsync(slug, ct);
    }
}
