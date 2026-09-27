using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// 训练数据集仓储：<c>training_datasets</c>，唯一索引 <c>(Type, Name)</c>——
/// 重名插入/改名直接由数据库抛 <see cref="MongoWriteException"/>（DuplicateKey），由 API 层映射 409。
/// </summary>
public sealed class TrainingDatasetRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TrainingDatasetRecord> _collection =
        database.GetCollection<TrainingDatasetRecord>(MongoCollectionNames.TrainingDatasets);

    /// <summary>按类型列举数据集，CreatedAt 升序。</summary>
    public async Task<List<TrainingDatasetRecord>> ListAsync(string type, CancellationToken ct = default) =>
        await _collection.Find(d => d.Type == type)
            .SortBy(d => d.CreatedAt)
            .ToListAsync(ct);

    /// <summary>按 id 读取单个数据集；不存在返回 null（避免为读一条而全量列举）。</summary>
    public async Task<TrainingDatasetRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(d => d.Id == id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// 插入数据集：<paramref name="record"/>.<c>Name</c> 落库前 <c>Trim()</c>
    /// （API 层已 trim，此处兜底保证 (Type, Name) 唯一索引口径一致）；同 (Type, Name) 重复抛 DuplicateKey。
    /// </summary>
    public async Task<TrainingDatasetRecord> InsertAsync(
        TrainingDatasetRecord record, CancellationToken ct = default)
    {
        record.Name = record.Name.Trim();
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    /// <summary>改名（落库前 Trim）并刷新 UpdatedAt；未命中返回 false；重名抛 DuplicateKey。</summary>
    public async Task<bool> RenameAsync(ObjectId id, string name, CancellationToken ct = default)
    {
        var update = Builders<TrainingDatasetRecord>.Update
            .Set(d => d.Name, name.Trim())
            .Set(d => d.UpdatedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(d => d.Id == id, update, cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    /// <summary>删除数据集（条目级联清理由调用方先行执行）；未命中返回 false。</summary>
    public async Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        var result = await _collection.DeleteOneAsync(d => d.Id == id, cancellationToken: ct);
        return result.DeletedCount > 0;
    }
}
