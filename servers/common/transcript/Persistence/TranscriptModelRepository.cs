using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptModelRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TranscriptModelRecord> _collection =
        database.GetCollection<TranscriptModelRecord>(TranscriptCollectionNames.TranscriptModels);

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

    public Task UpdateAsync(
        ObjectId id,
        string? name = null,
        string? provider = null,
        string? type = null,
        string? apiKey = null,
        string? model = null,
        bool? enabled = null,
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
        if (model is not null)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.Model, model));
        }
        if (enabled.HasValue)
        {
            updates.Add(Builders<TranscriptModelRecord>.Update.Set(m => m.Enabled, enabled.Value));
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
}
