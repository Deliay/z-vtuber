using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Cookie.Persistence;

public class CookieSessionRepository : ICookieSessionRepository
{
    private readonly IMongoCollection<CookieSessionRecord> _collection;

    public CookieSessionRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<CookieSessionRecord>("bilibili_cookie_sessions");
    }

    public async Task<CookieSessionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default)
    {
        return await _collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task InsertAsync(CookieSessionRecord record, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(record, cancellationToken: ct);
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
}