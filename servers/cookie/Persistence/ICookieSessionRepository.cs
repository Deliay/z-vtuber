using MongoDB.Bson;

namespace Z.Vtuber.Cookie.Persistence;

public interface ICookieSessionRepository
{
    Task<CookieSessionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default);
    Task InsertAsync(CookieSessionRecord record, CancellationToken ct = default);
    Task UpdateAsync(CookieSessionRecord record, CancellationToken ct = default);
    Task DeleteAsync(ObjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<CookieSessionRecord>> GetByStatusAsync(string status, CancellationToken ct = default);
}