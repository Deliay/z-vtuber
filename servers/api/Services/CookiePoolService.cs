using MongoDB.Driver;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public interface ICookiePoolService
{
    Task AddAsync(string cookie, CancellationToken ct = default);
    Task<CookieRecord> NextAsync(CancellationToken ct = default);
    Task MarkOfflineAsync(string cookie, CancellationToken ct = default);
    Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default);
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

    public async Task AddAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentException("Cookie must not be empty.", nameof(cookie));
        }

        var exists = await _collection
            .Find(c => c.Cookie == cookie)
            .AnyAsync(ct);
        if (exists)
        {
            return;
        }

        await _collection.InsertOneAsync(new CookieRecord
        {
            Cookie = cookie,
            Status = CookieStatus.Active,
        }, cancellationToken: ct);
    }

    public async Task<CookieRecord> NextAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active);
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
        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active);
        return await _collection.Find(filter).ToListAsync(ct);
    }
}
