using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptModelRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TranscriptModelRecord> _collection =
        database.GetCollection<TranscriptModelRecord>(MongoCollectionNames.TranscriptModels);

    public async Task<ObjectId> InsertAsync(TranscriptModelRecord record, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record.Id;
    }

    public async Task<IReadOnlyList<TranscriptModelRecord>> ListAsync(CancellationToken ct = default) =>
        await _collection.Find(FilterDefinition<TranscriptModelRecord>.Empty)
            .SortByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

    public async Task<TranscriptModelRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct);

    public async Task<TranscriptModelRecord?> GetDefaultAsync(CancellationToken ct = default) =>
        await _collection.Find(m => m.IsDefault).FirstOrDefaultAsync(ct);

    public Task UpdateAsync(
        ObjectId id,
        string? name = null,
        string? provider = null,
        string? type = null,
        string? apiKey = null,
        bool? enabled = null,
        bool? isDefault = null,
        int? maxConcurrency = null,
        CancellationToken ct = default)
    {
        var updates = new List<UpdateDefinition<TranscriptModelRecord>>();
        if (name is not null)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.Name, name));
        }
        if (provider is not null)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.Provider, provider));
        }
        if (type is not null)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.Type, type));
        }
        if (apiKey is not null)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.ApiKey, apiKey));
        }
        if (enabled.HasValue)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.Enabled, enabled.Value));
        }
        if (isDefault.HasValue)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.IsDefault, isDefault.Value));
        }
        if (maxConcurrency.HasValue)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.MaxConcurrency, maxConcurrency.Value));
        }

        if (updates.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _collection.UpdateOneAsync(
            m => m.Id == id, Builders<TranscriptModelRecord>.Update.Combine(updates), cancellationToken: ct);
    }

    /// <summary>
    /// 原子切换默认位：在同一事务内清除其他模型的 IsDefault 并置位当前模型。
    /// </summary>
    public async Task SetDefaultAsync(ObjectId id, CancellationToken ct = default)
    {
        using var session = await _collection.Database.Client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        try
        {
            var clearFilter = Builders<TranscriptModelRecord>.Filter.And(
                Builders<TranscriptModelRecord>.Filter.Eq(m => m.IsDefault, true),
                Builders<TranscriptModelRecord>.Filter.Ne(m => m.Id, id));
            await _collection.UpdateManyAsync(
                session, clearFilter,
                Builders<TranscriptModelRecord>.Update.Set(m => m.IsDefault, false),
                cancellationToken: ct);

            await _collection.UpdateOneAsync(
                session, Builders<TranscriptModelRecord>.Filter.Eq(m => m.Id, id),
                Builders<TranscriptModelRecord>.Update.Set(m => m.IsDefault, true),
                cancellationToken: ct);

            await session.CommitTransactionAsync(ct);
        }
        catch
        {
            await session.AbortTransactionAsync(ct);
            throw;
        }
    }
}
