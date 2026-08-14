using MongoDB.Driver;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public interface ISubscriptionService
{
    event Action<long>? Subscribed;
    Task<IReadOnlyList<long>> GetMidsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionRecord>> GetAllAsync(CancellationToken ct = default);
    Task SubscribeAsync(long mid, CancellationToken ct = default);
    Task<bool> UnsubscribeAsync(long mid, CancellationToken ct = default);
}

public sealed class SubscriptionService(IMongoDatabase database) : ISubscriptionService
{
    private readonly IMongoCollection<SubscriptionRecord> _collection =
        database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);

    public event Action<long>? Subscribed;

    public async Task<IReadOnlyList<long>> GetMidsAsync(CancellationToken ct = default)
    {
        var projection = Builders<SubscriptionRecord>.Projection.Include(s => s.Mid);
        var records = await _collection.Find(FilterDefinition<SubscriptionRecord>.Empty)
            .Project<SubscriptionRecord>(projection)
            .ToListAsync(ct);
        return records.Select(s => s.Mid).ToList();
    }

    public async Task<IReadOnlyList<SubscriptionRecord>> GetAllAsync(CancellationToken ct = default)
    {
        return await _collection.Find(FilterDefinition<SubscriptionRecord>.Empty)
            .SortBy(s => s.SubscribedAt)
            .ToListAsync(ct);
    }

    public async Task SubscribeAsync(long mid, CancellationToken ct = default)
    {
        if (mid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mid), "mid must be positive.");
        }

        var filter = Builders<SubscriptionRecord>.Filter.Eq(s => s.Mid, mid);
        var update = Builders<SubscriptionRecord>.Update.SetOnInsert(s => s.SubscribedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

        if (result.UpsertedId is not null)
        {
            Subscribed?.Invoke(mid);
        }
    }

    public async Task<bool> UnsubscribeAsync(long mid, CancellationToken ct = default)
    {
        var filter = Builders<SubscriptionRecord>.Filter.Eq(s => s.Mid, mid);
        var result = await _collection.DeleteOneAsync(filter, ct);
        return result.DeletedCount > 0;
    }
}
