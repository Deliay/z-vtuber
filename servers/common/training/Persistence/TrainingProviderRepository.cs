using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// Provider 仓储：<c>training_providers</c>，唯一索引 <c>Name</c>——
/// 重名插入/改名由数据库抛 <see cref="MongoWriteException"/>（DuplicateKey），API 层映射 409。
/// </summary>
public sealed class TrainingProviderRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingProviderRecord> _collection =
        database.GetCollection<TrainingProviderRecord>(TrainingCollectionNames.TrainingProviders);

    /// <summary>列举 Provider，CreatedAt 升序。</summary>
    public async Task<List<TrainingProviderRecord>> ListAsync(CancellationToken ct = default) =>
        await _collection.Find(Builders<TrainingProviderRecord>.Filter.Empty)
            .SortBy(p => p.CreatedAt)
            .ToListAsync(ct);

    /// <summary>按 id 读取；不存在返回 null。</summary>
    public async Task<TrainingProviderRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(p => p.Id == id).FirstOrDefaultAsync(ct);

    /// <summary>插入 Provider（落库前 Trim）；重名抛 DuplicateKey。</summary>
    public async Task<TrainingProviderRecord> InsertAsync(
        TrainingProviderRecord record, CancellationToken ct = default)
    {
        record.Name = record.Name.Trim();
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    /// <summary>按更新定义更新；未命中返回 null。重名抛 DuplicateKey。</summary>
    public async Task<TrainingProviderRecord?> UpdateAsync(
        ObjectId id, UpdateDefinition<TrainingProviderRecord> update, CancellationToken ct = default)
    {
        var result = await _collection.UpdateOneAsync(p => p.Id == id, update, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return null;
        }

        return await _collection.Find(p => p.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>删除 Provider；未命中返回 false。</summary>
    public async Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        var result = await _collection.DeleteOneAsync(p => p.Id == id, cancellationToken: ct);
        return result.DeletedCount > 0;
    }
}
