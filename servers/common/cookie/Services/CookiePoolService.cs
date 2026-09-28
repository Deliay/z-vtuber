using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Services;

public interface ICookiePoolService
{
    Task<CookieRecord> AddAsync(string cookie, string? accountId = null, CancellationToken ct = default);
    Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default);
    Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default);
    Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default);
    Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default);
    Task<CookieRecord?> FindByCookieIdAsync(string cookieId, CancellationToken ct = default);
    Task UpdateAsync(string cookieId, UpdateDefinition<CookieRecord> update, CancellationToken ct = default);
}

public sealed class CookiePoolService(IMongoDatabase database, string cookiesCollection = CookieCollectionNames.Cookies) : ICookiePoolService
{
    private readonly IMongoCollection<CookieRecord> _collection =
        database.GetCollection<CookieRecord>(cookiesCollection);

    public async Task<CookieRecord> AddAsync(string cookie, string? accountId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentException("Cookie must not be empty.", nameof(cookie));
        }

        var existing = await _collection
            .Find(c => c.Cookie == cookie)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var record = new CookieRecord
        {
            Cookie = cookie,
            AccountId = accountId,
            Status = CookieStatus.Active,
        };
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    public async Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default)
    {
        var filter = Builders<CookieRecord>.Filter.And(
            Builders<CookieRecord>.Filter.Eq(c => c.Cookie, cookie),
            Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active));
        var update = Builders<CookieRecord>.Update
            .Set(c => c.Status, CookieStatus.Offline)
            .Set(c => c.LastCheckedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<CookieRecord>.Filter.And(
            Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active),
            Builders<CookieRecord>.Filter.Or(
                Builders<CookieRecord>.Filter.Eq(c => c.CooldownUntil, null),
                Builders<CookieRecord>.Filter.Lte(c => c.CooldownUntil, now)));
        return await _collection.Find(filter).ToListAsync(ct);
    }

    public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default)
    {
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Cookie, cookie);
        var update = Builders<CookieRecord>.Update
            .Set(c => c.CooldownUntil, DateTime.UtcNow + duration);
        return _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default)
    {
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Cookie, cookie);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<CookieRecord?> FindByCookieIdAsync(string cookieId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(cookieId, out var objectId))
        {
            return null;
        }
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Id, objectId);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task UpdateAsync(string cookieId, UpdateDefinition<CookieRecord> update, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(cookieId, out var objectId))
        {
            return;
        }
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Id, objectId);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
