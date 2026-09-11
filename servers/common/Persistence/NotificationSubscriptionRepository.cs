using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public interface INotificationSubscriptionRepository
{
    Task<IReadOnlyList<NotificationSubscriptionRecord>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NotificationSubscriptionRecord>> GetByEventTypeAsync(string eventType, CancellationToken ct = default);
    Task<NotificationSubscriptionRecord> InsertAsync(NotificationSubscriptionRecord record, CancellationToken ct = default);
    Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default);
    Task<NotificationSubscriptionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default);
}

public sealed class NotificationSubscriptionRepository(IMongoDatabase database) : INotificationSubscriptionRepository
{
    private readonly IMongoCollection<NotificationSubscriptionRecord> _collection =
        database.GetCollection<NotificationSubscriptionRecord>(MongoCollectionNames.NotificationSubscriptions);

    public async Task<IReadOnlyList<NotificationSubscriptionRecord>> ListAsync(CancellationToken ct = default)
    {
        return await _collection.Find(_ => true).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationSubscriptionRecord>> GetByEventTypeAsync(string eventType, CancellationToken ct = default)
    {
        var filter = Builders<NotificationSubscriptionRecord>.Filter.Eq(r => r.EventType, eventType);
        return await _collection.Find(filter).ToListAsync(ct);
    }

    public async Task<NotificationSubscriptionRecord> InsertAsync(NotificationSubscriptionRecord record, CancellationToken ct = default)
    {
        if (record.Id == ObjectId.Empty)
        {
            record.Id = ObjectId.GenerateNewId();
        }

        if (record.CreatedAt == default)
        {
            record.CreatedAt = DateTime.UtcNow;
        }

        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    public async Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        var result = await _collection.DeleteOneAsync(r => r.Id == id, ct);
        return result.DeletedCount > 0;
    }

    public async Task<NotificationSubscriptionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default)
    {
        return await _collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct);
    }
}
