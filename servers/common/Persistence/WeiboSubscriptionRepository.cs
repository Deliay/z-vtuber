using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class WeiboSubscriptionRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<WeiboSubscriptionRecord> _collection =
        database.GetCollection<WeiboSubscriptionRecord>(MongoCollectionNames.WeiboSubscriptions);

    public async Task<bool> UpsertAsync(long uid, CancellationToken ct = default)
    {
        var filter = Builders<WeiboSubscriptionRecord>.Filter.Eq(s => s.Uid, uid);
        var update = Builders<WeiboSubscriptionRecord>.Update.SetOnInsert(s => s.SubscribedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
        return result.UpsertedId is not null;
    }

    public async Task<bool> DeleteAsync(long uid, CancellationToken ct = default)
    {
        var filter = Builders<WeiboSubscriptionRecord>.Filter.Eq(s => s.Uid, uid);
        var result = await _collection.DeleteOneAsync(filter, ct);
        return result.DeletedCount > 0;
    }

    public async Task<IReadOnlyList<long>> GetUidsAsync(CancellationToken ct = default)
    {
        var projection = Builders<WeiboSubscriptionRecord>.Projection.Include(s => s.Uid);
        var records = await _collection.Find(FilterDefinition<WeiboSubscriptionRecord>.Empty)
            .Project<WeiboSubscriptionRecord>(projection)
            .ToListAsync(ct);
        return records.Select(s => s.Uid).ToList();
    }

    public async Task<IReadOnlyList<WeiboSubscriptionRecord>> GetAllAsync(CancellationToken ct = default)
    {
        return await _collection.Find(FilterDefinition<WeiboSubscriptionRecord>.Empty)
            .SortBy(s => s.SubscribedAt)
            .ToListAsync(ct);
    }
}
