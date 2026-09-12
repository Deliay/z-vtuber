using MongoDB.Bson;

namespace Z.Vtuber.Cookie.Persistence;

public interface ICookieSessionRepository
{
    Task<CookieSessionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default);
    Task<CookieSessionRecord?> GetByCookieIdAsync(string cookieId, CancellationToken ct = default);
    Task InsertAsync(CookieSessionRecord record, CancellationToken ct = default);
    Task<bool> TransitionStatusAsync(ObjectId id, string expectedStatus, string newStatus, CancellationToken ct = default);
    Task UpdateAsync(CookieSessionRecord record, CancellationToken ct = default);
    Task DeleteAsync(ObjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<CookieSessionRecord>> GetByStatusAsync(string status, CancellationToken ct = default);
    Task<IReadOnlyList<CookieSessionRecord>> GetPendingExpiredAsync(DateTime now, CancellationToken ct = default);
    Task IncrementRefreshFailureAsync(ObjectId id, CancellationToken ct = default);
}
