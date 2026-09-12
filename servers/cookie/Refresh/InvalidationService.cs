using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MongoDB.Bson;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Persistence;

namespace Z.Vtuber.Cookie.Refresh;

public interface IInvalidationService
{
    Task<InvalidationResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default);
}

public class InvalidationService : IInvalidationService
{
    private readonly ICookieSessionRepository _sessionRepository;
    private readonly ICookiePoolService _cookiePool;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly Z.Vtuber.Cookie.Browser.IBrowserSessionManager _browserSessionManager;
    private readonly ICookieInvalidEventPublisher _eventPublisher;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<InvalidationService> _logger;

    public InvalidationService(
        ICookieSessionRepository sessionRepository,
        ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        Z.Vtuber.Cookie.Browser.IBrowserSessionManager browserSessionManager,
        ICookieInvalidEventPublisher eventPublisher,
        IOptions<CookieServiceOptions> options,
        ILogger<InvalidationService> logger)
    {
        _sessionRepository = sessionRepository;
        _cookiePool = cookiePool;
        _lockProvider = lockProvider;
        _browserSessionManager = browserSessionManager;
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InvalidationResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        // Find cookie in common pool
        var cookieRecord = await _cookiePool.FindByCookieAsync(cookieId, ct);
        if (cookieRecord is null)
        {
            return InvalidationResult.NotFound;
        }

        // Find session by cookieId
        var session = await _sessionRepository.GetByCookieIdAsync(cookieId, ct);
        if (session is null)
        {
            // No session associated - historical cookie, mark offline directly
            await _cookiePool.MarkOfflineAsync(cookieId, ct);
            try
            {
                await _eventPublisher.PublishAsync(cookieId, "no_session", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish invalid event for cookie {CookieId}", cookieId);
            }
            return InvalidationResult.SessionInvalid;
        }

        // Acquire distributed lock for serializing invalidation
        var lockKey = $"lock:cookie-invalidate:{cookieId}";
        var lockTtl = TimeSpan.FromSeconds(_options.RefreshLockTtlSeconds);
        var lockWait = TimeSpan.FromMilliseconds(_options.LockAcquireWaitMs);

        await using var lockHandle = await _lockProvider.AcquireAsync(lockKey, lockTtl, lockWait, ct);
        if (lockHandle is null)
        {
            _logger.LogWarning("Could not acquire invalidation lock for cookie {CookieId}", cookieId);
            return InvalidationResult.Timeout;
        }

        // Mark cookie offline
        await _cookiePool.MarkOfflineAsync(cookieId, ct);

        // Mark session as invalid if active
        if (session.Status == "active")
        {
            await _sessionRepository.TransitionStatusAsync(session.Id, "active", "invalid", ct);
        }

        // Publish invalid event
        try
        {
            await _eventPublisher.PublishAsync(cookieId, "report_invalid", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish invalid event for cookie {CookieId}", cookieId);
        }

        // Clean up browser context
        await _browserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);

        return InvalidationResult.SessionInvalid;
    }
}

public enum InvalidationResult
{
    NotFound,
    SessionInvalid,
    Refreshed,
    Timeout,
}
