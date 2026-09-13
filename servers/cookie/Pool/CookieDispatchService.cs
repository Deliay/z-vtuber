using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Cookie.Pool;

public interface ICookieDispatchService
{
    Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default);
    Task<bool> SetCooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default);
    Task<IReadOnlyList<CookieItem>> GetAvailableAsync(CancellationToken ct = default);
}

public class CookieDispatchService : ICookieDispatchService
{
    private readonly IMongoCollection<CookieRecord> _collection;
    private readonly IMongoCollection<CookieSessionRecord> _sessionCollection;

    public CookieDispatchService(IMongoDatabase database)
    {
        _collection = database.GetCollection<CookieRecord>(MongoCollectionNames.Cookies);
        _sessionCollection = database.GetCollection<CookieSessionRecord>(MongoCollectionNames.CookieSessions);
    }

    public async Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default)
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

        if (record is null)
        {
            throw new NoAvailableCookieException();
        }

        return new CookieCheckoutResult
        {
            CookieId = record.Id.ToString(),
            AccountId = record.AccountId,
            Cookie = record.Cookie,
        };
    }

    public async Task<bool> SetCooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(cookieId, out var objectId))
        {
            return false;
        }

        var filter = Builders<CookieRecord>.Filter.Eq(c => c.Id, objectId);
        var update = Builders<CookieRecord>.Update
            .Set(c => c.CooldownUntil, DateTime.UtcNow + duration);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async Task<IReadOnlyList<CookieItem>> GetAvailableAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<CookieRecord>.Filter.And(
            Builders<CookieRecord>.Filter.Eq(c => c.Status, CookieStatus.Active),
            Builders<CookieRecord>.Filter.Or(
                Builders<CookieRecord>.Filter.Eq(c => c.CooldownUntil, null),
                Builders<CookieRecord>.Filter.Lte(c => c.CooldownUntil, now)));
        var sort = Builders<CookieRecord>.Sort.Ascending(c => c.LastUsedAt);

        var records = await _collection.Find(filter).Sort(sort).ToListAsync(ct);

        var accountIds = records
            .Where(r => !string.IsNullOrEmpty(r.AccountId))
            .Select(r => r.AccountId!)
            .Distinct()
            .ToList();

        var sessions = accountIds.Count > 0
            ? await _sessionCollection.Find(s => accountIds.Contains(s.Id.ToString())).ToListAsync(ct)
            : [];

        var sessionLookup = sessions.ToDictionary(s => s.Id.ToString(), s => s.Uname);

        return records.Select(r => new CookieItem
        {
            CookieId = r.Id.ToString(),
            AccountId = r.AccountId,
            Uname = r.AccountId != null && sessionLookup.TryGetValue(r.AccountId, out var uname) ? uname : null,
            Status = r.Status,
            LastCheckedAt = r.LastCheckedAt,
            LastUsedAt = r.LastUsedAt,
            CooldownUntil = r.CooldownUntil,
            LastRefreshAt = r.LastRefreshAt,
        }).ToList();
    }
}

public class CookieCheckoutResult
{
    public string CookieId { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string Cookie { get; set; } = string.Empty;
}

public class CookieItem
{
    public string CookieId { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string? Uname { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? CooldownUntil { get; set; }
    public DateTime? LastRefreshAt { get; set; }
}
