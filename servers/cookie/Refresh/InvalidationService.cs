using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Cookie.Refresh;

public interface IInvalidationService
{
    Task<InvalidationResultValue> ReportInvalidAsync(string cookieId, CancellationToken ct = default);
}

public class InvalidationResultValue
{
    public InvalidationResult Result { get; set; }
    public string? NewCookieId { get; set; }
    public string? NewCookie { get; set; }
    public string? AccountId { get; set; }
}

public class InvalidationService : IInvalidationService
{
    private readonly ICookieSessionRepository _sessionRepository;
    private readonly ICookiePoolService _cookiePool;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly IBrowserSessionManager _browserSessionManager;
    private readonly ICookieInvalidEventPublisher _eventPublisher;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<InvalidationService> _logger;

    public InvalidationService(
        ICookieSessionRepository sessionRepository,
        ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        IBrowserSessionManager browserSessionManager,
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

    public async Task<InvalidationResultValue> ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        var cookieRecord = await _cookiePool.FindByCookieIdAsync(cookieId, ct);
        if (cookieRecord is null)
        {
            return new InvalidationResultValue { Result = InvalidationResult.NotFound };
        }

        var session = await _sessionRepository.GetByCookieIdAsync(cookieId, ct);
        if (session is null)
        {
            await _cookiePool.MarkOfflineAsync(cookieRecord.Cookie, ct);
            try
            {
                await _eventPublisher.PublishAsync(cookieId, "no_session", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish invalid event for cookie {CookieId}", cookieId);
            }
            return new InvalidationResultValue { Result = InvalidationResult.SessionInvalid, AccountId = cookieRecord.AccountId };
        }

        var lockKey = $"lock:cookie-refresh:{session.Id}";
        var lockTtl = TimeSpan.FromSeconds(_options.ReportInvalidTimeoutSeconds);
        var lockWait = TimeSpan.FromMilliseconds(_options.LockAcquireWaitMs);

        await using var lockHandle = await _lockProvider.AcquireAsync(lockKey, lockTtl, lockWait, ct);
        if (lockHandle is null)
        {
            _logger.LogWarning("Could not acquire invalidation lock for session {SessionId}", session.Id);
            return new InvalidationResultValue { Result = InvalidationResult.Timeout };
        }

        if (session.Status == "active" && session.LastRefreshAt > DateTime.UtcNow.AddSeconds(-30))
        {
            _logger.LogDebug("Session {SessionId} was recently refreshed, skipping invalidation (dedup)", session.Id);
            return new InvalidationResultValue { Result = InvalidationResult.Refreshed, AccountId = cookieRecord.AccountId };
        }

        var refreshDeadline = DateTime.UtcNow.AddSeconds(_options.ReportInvalidTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(refreshDeadline - DateTime.UtcNow);

        try
        {
            await _browserSessionManager.RefreshSessionAsync(session.Id.ToString(), timeoutCts.Token);

            var newCookie = await _browserSessionManager.ExtractCookiesAsync(session.Id.ToString(), timeoutCts.Token);

            await _cookiePool.MarkOfflineAsync(cookieRecord.Cookie, ct);
            var newCookieRecord = await _cookiePool.AddAsync(newCookie, session.Id.ToString(), ct);

            session.Cookie = newCookie;
            session.CookieId = newCookieRecord.Id.ToString();
            session.LastRefreshAt = DateTime.UtcNow;
            session.ConsecutiveRefreshFailures = 0;
            await _sessionRepository.UpdateAsync(session, ct);

            try
            {
                await _eventPublisher.PublishAsync(cookieId, "refreshed", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish refreshed event for cookie {CookieId}", cookieId);
            }

            return new InvalidationResultValue
            {
                Result = InvalidationResult.Refreshed,
                NewCookieId = newCookieRecord.Id.ToString(),
                NewCookie = newCookie,
                AccountId = cookieRecord.AccountId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Immediate refresh failed for session {SessionId}", session.Id);

            await _cookiePool.MarkOfflineAsync(cookieRecord.Cookie, ct);

            if (session.Status == "active")
            {
                await _sessionRepository.TransitionStatusAsync(session.Id, "active", "invalid", ct);
            }

            try
            {
                await _eventPublisher.PublishAsync(cookieId, "session_invalid", ct);
            }
            catch (Exception pubEx)
            {
                _logger.LogWarning(pubEx, "Failed to publish invalid event for cookie {CookieId}", cookieId);
            }

            await _browserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);

            return new InvalidationResultValue { Result = InvalidationResult.SessionInvalid, AccountId = cookieRecord.AccountId };
        }
    }
}

public enum InvalidationResult
{
    NotFound,
    SessionInvalid,
    Refreshed,
    Timeout,
}
