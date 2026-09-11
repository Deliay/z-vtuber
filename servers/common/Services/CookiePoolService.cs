using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Services;

public interface ICookiePoolService
{
    Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default);
    Task<CookieRecord> NextAsync(CancellationToken ct = default);
    Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default);
    Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default);
    Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default);
    Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default);
}

public sealed class NoAvailableCookieException : InvalidOperationException
{
    public NoAvailableCookieException()
        : base("No active cookie available in the pool; crawling is paused until a valid cookie is added.")
    {
    }
}

public sealed class CookiePoolService(IMongoDatabase database, IServiceProvider serviceProvider) : ICookiePoolService
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
        while (true)
        {
            var now = DateTime.UtcNow;
            var filter = Builders<CookieRecord>.Filter.And(
                Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active),
                Builders<CookieRecord>.Filter.Or(
                    Builders<CookieRecord>.Filter.Eq(c => c.CooldownUntil, null),
                    Builders<CookieRecord>.Filter.Lte(c => c.CooldownUntil, now)));
            var update = Builders<CookieRecord>.Update.Set(c => c.LastUsedAt, now);
            var sort = Builders<CookieRecord>.Sort.Ascending(c => c.LastUsedAt);

            // ReturnDocument.Before：取出更新前的记录，用以判定「首次使用」（LastUsedAt 为 null）。
            var record = await _collection.FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<CookieRecord>
                {
                    Sort = sort,
                    ReturnDocument = ReturnDocument.Before,
                },
                ct);

            if (record is null)
            {
                throw new NoAvailableCookieException();
            }

            // 非首次检出：行为完全不变，不做额外 nav 调用。
            if (record.LastUsedAt is not null)
            {
                return record;
            }

            // 首次使用：先 nav 确认可用再交出 Cookie。通过 IServiceProvider 惰性解析，
            // 规避 CookiePool → IBiliApiClient → CookiePool 的构造循环依赖。
            var probe = serviceProvider.GetService<ICookieNavProbeService>();
            if (probe is null)
            {
                return record;
            }

            var outcome = await probe.ProbeAsync(record, ct);
            if (outcome == CookieProbeOutcome.Success)
            {
                return record;
            }

            // 探测失败（该 Cookie 已被标记 offline/cooldown 或处于退避），继续取下一个可用 Cookie。
        }
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
}
