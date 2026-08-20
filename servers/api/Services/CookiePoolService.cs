using MongoDB.Driver;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public interface ICookiePoolService
{
    Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default);
    Task<CookieRecord> NextAsync(CancellationToken ct = default);
    Task MarkOfflineAsync(string cookie, CancellationToken ct = default);
    Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default);
    Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default);
}

public sealed class NoAvailableCookieException : InvalidOperationException
{
    public NoAvailableCookieException()
        : base("No active cookie available in the pool; crawling is paused until a valid cookie is added.")
    {
    }
}

public sealed class CookiePoolService(IMongoDatabase database) : ICookiePoolService
{
    private readonly IMongoCollection<CookieRecord> _collection =
        database.GetCollection<CookieRecord>(MongoCollectionNames.Cookies);

    public async Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default)
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
            Status = CookieStatus.Active,
        };
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record;
    }

    public async Task<CookieRecord> NextAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<CookieRecord>.Filter.And(
            Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active),
            Builders<CookieRecord>.Filter.Or(
                Builders<CookieRecord>.Filter.Eq(c => c.CooldownUntil, null),
                Builders<CookieRecord>.Filter.Lte(c => c.CooldownUntil, now)));
        var update = Builders<CookieRecord>.Update.Set(c => c.LastUsedAt, now);
        var sort = Builders<CookieRecord>.Sort.Ascending(c => c.LastUsedAt);

        var record = await _collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<CookieRecord>
            {
                Sort = sort,
                ReturnDocument = ReturnDocument.After,
            },
            ct);

        return record ?? throw new NoAvailableCookieException();
    }

    public Task MarkOfflineAsync(string cookie, CancellationToken ct = default)
    {
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Cookie, cookie);
        var update = Builders<CookieRecord>.Update
            .Set(c => c.Status, CookieStatus.Offline)
            .Set(c => c.LastCheckedAt, DateTime.UtcNow);
        return _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
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
}
