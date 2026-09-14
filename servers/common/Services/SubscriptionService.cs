using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Services;

public interface ISubscriptionService
{
    event Action<long, SubscriptionType>? Subscribed;
    Task<IReadOnlyList<long>> GetMidsAsync(SubscriptionType type, CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionRecord>> GetAllAsync(CancellationToken ct = default);
    Task SubscribeAsync(long mid, SubscriptionType type, CancellationToken ct = default);
    Task<bool> UnsubscribeAsync(long mid, SubscriptionType type, CancellationToken ct = default);

    /// <summary>
    /// 由跨进程订阅事件桥接触发（crawler 侧），用于让本进程内监听 <see cref="Subscribed"/> 的组件响应远端订阅创建。
    /// </summary>
    void RaiseSubscribed(long mid, SubscriptionType type);
}

public sealed class SubscriptionService(IMongoDatabase database) : ISubscriptionService
{
    private readonly IMongoCollection<SubscriptionRecord> _collection =
        database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);

    public event Action<long, SubscriptionType>? Subscribed;

    public void RaiseSubscribed(long mid, SubscriptionType type) => Subscribed?.Invoke(mid, type);

    public async Task<IReadOnlyList<long>> GetMidsAsync(SubscriptionType type, CancellationToken ct = default)
    {
        var filter = Builders<SubscriptionRecord>.Filter.Eq(s => s.Type, type);
        var projection = Builders<SubscriptionRecord>.Projection.Include(s => s.Mid);
        var records = await _collection.Find(filter)
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

    public async Task SubscribeAsync(long mid, SubscriptionType type, CancellationToken ct = default)
    {
        if (mid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mid), "mid must be positive.");
        }

        var filter = Builders<SubscriptionRecord>.Filter.And(
            Builders<SubscriptionRecord>.Filter.Eq(s => s.Mid, mid),
            Builders<SubscriptionRecord>.Filter.Eq(s => s.Type, type));
        var update = Builders<SubscriptionRecord>.Update.SetOnInsert(s => s.SubscribedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

        if (result.UpsertedId is not null)
        {
            Subscribed?.Invoke(mid, type);
        }
    }

    public async Task<bool> UnsubscribeAsync(long mid, SubscriptionType type, CancellationToken ct = default)
    {
        var filter = Builders<SubscriptionRecord>.Filter.And(
            Builders<SubscriptionRecord>.Filter.Eq(s => s.Mid, mid),
            Builders<SubscriptionRecord>.Filter.Eq(s => s.Type, type));
        var result = await _collection.DeleteOneAsync(filter, ct);
        return result.DeletedCount > 0;
    }
}
