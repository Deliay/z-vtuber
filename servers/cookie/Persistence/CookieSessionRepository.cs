using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Cookie.Persistence;

public class CookieSessionRepository : ICookieSessionRepository
{
    private readonly IMongoCollection<CookieSessionRecord> _collection;

    public CookieSessionRepository(IMongoDatabase database, string collectionName = "bilibili_cookie_sessions")
    {
        _collection = database.GetCollection<CookieSessionRecord>(collectionName);
    }

    public async Task<CookieSessionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default)
    {
        return await _collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task<CookieSessionRecord?> GetByCookieIdAsync(string cookieId, CancellationToken ct = default)
    {
        return await _collection.Find(r => r.CookieId == cookieId).FirstOrDefaultAsync(ct);
    }

    public async Task InsertAsync(CookieSessionRecord record, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(record, cancellationToken: ct);
    }

    public async Task<bool> TransitionStatusAsync(ObjectId id, string expectedStatus, string newStatus, CancellationToken ct = default)
    {
        var filter = Builders<CookieSessionRecord>.Filter.And(
            Builders<CookieSessionRecord>.Filter.Eq(r => r.Id, id),
            Builders<CookieSessionRecord>.Filter.Eq(r => r.Status, expectedStatus));
        var update = Builders<CookieSessionRecord>.Update.Set(r => r.Status, newStatus);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async Task UpdateAsync(CookieSessionRecord record, CancellationToken ct = default)
    {
        await _collection.ReplaceOneAsync(r => r.Id == record.Id, record, cancellationToken: ct);
    }

    public async Task DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        await _collection.DeleteOneAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<CookieSessionRecord>> GetByStatusAsync(string status, CancellationToken ct = default)
    {
        return await _collection.Find(r => r.Status == status).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CookieSessionRecord>> GetPendingExpiredAsync(DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<CookieSessionRecord>.Filter.And(
            Builders<CookieSessionRecord>.Filter.Eq(r => r.Status, "pending_login"),
            Builders<CookieSessionRecord>.Filter.Lt(r => r.ExpiresAt, now));
        return await _collection.Find(filter).ToListAsync(ct);
    }

    public async Task IncrementRefreshFailureAsync(ObjectId id, CancellationToken ct = default)
    {
        var update = Builders<CookieSessionRecord>.Update.Inc(r => r.ConsecutiveRefreshFailures, 1);
        await _collection.UpdateOneAsync(r => r.Id == id, update, cancellationToken: ct);
    }
}
