using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Cookie.Refresh;

public class SessionRefreshJob : BackgroundService
{
    private readonly ICookieSessionRepository _sessionRepository;
    private readonly Z.Vtuber.Cookie.Browser.IBrowserSessionManager _browserSessionManager;
    private readonly ICookiePoolService _cookiePool;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly ICookieInvalidEventPublisher _eventPublisher;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<SessionRefreshJob> _logger;

    public SessionRefreshJob(
        ICookieSessionRepository sessionRepository,
        Z.Vtuber.Cookie.Browser.IBrowserSessionManager browserSessionManager,
        ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        ICookieInvalidEventPublisher eventPublisher,
        IOptions<CookieServiceOptions> options,
        ILogger<SessionRefreshJob> logger)
    {
        _sessionRepository = sessionRepository;
        _browserSessionManager = browserSessionManager;
        _cookiePool = cookiePool;
        _lockProvider = lockProvider;
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionRefreshJob started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshActiveSessions(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SessionRefreshJob loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task RefreshActiveSessions(CancellationToken ct)
    {
        var activeSessions = await _sessionRepository.GetByStatusAsync("active", ct);
        var refreshThreshold = DateTime.UtcNow.AddMinutes(-_options.RefreshIntervalMinutes);

        foreach (var session in activeSessions)
        {
            if (session.LastRefreshAt > refreshThreshold)
            {
                continue;
            }

            var lockKey = $"lock:cookie-refresh:{session.Id}";
            var lockTtl = TimeSpan.FromSeconds(_options.RefreshLockTtlSeconds);
            var lockWait = TimeSpan.FromMilliseconds(_options.LockAcquireWaitMs);

            await using var lockHandle = await _lockProvider.AcquireAsync(lockKey, lockTtl, lockWait, ct);
            if (lockHandle is null)
            {
                _logger.LogDebug("Could not acquire refresh lock for session {SessionId}", session.Id);
                continue;
            }

            try
            {
                await RefreshSessionAsync(session, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing session {SessionId}", session.Id);
            }
        }
    }

    private async Task RefreshSessionAsync(CookieSessionRecord session, CancellationToken ct)
    {
        try
        {
            await _browserSessionManager.RefreshSessionAsync(session.Id.ToString(), ct);
            var newCookie = await _browserSessionManager.ExtractCookiesAsync(session.Id.ToString(), ct);

            if (!string.IsNullOrEmpty(session.Cookie) && session.Cookie != newCookie)
            {
                await _cookiePool.MarkOfflineAsync(session.Cookie, ct);
            }

            var newCookieRecord = await _cookiePool.AddAsync(newCookie, session.Id.ToString(), ct);

            session.Cookie = newCookie;
            session.CookieId = newCookieRecord.Id.ToString();
            session.LastRefreshAt = DateTime.UtcNow;
            session.ConsecutiveRefreshFailures = 0;
            await _sessionRepository.UpdateAsync(session, ct);

            _logger.LogDebug("Refreshed session {SessionId} successfully", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh failed for session {SessionId}", session.Id);

            await _sessionRepository.IncrementRefreshFailureAsync(session.Id, ct);
            var updatedSession = await _sessionRepository.GetByIdAsync(session.Id, ct);

            if (updatedSession is not null && updatedSession.ConsecutiveRefreshFailures >= _options.MaxConsecutiveRefreshFailures)
            {
                _logger.LogWarning("Session {SessionId} exceeded max refresh failures, marking invalid", session.Id);
                await InvalidateSessionAsync(session, ct);
            }
        }
    }

    private async Task InvalidateSessionAsync(CookieSessionRecord session, CancellationToken ct)
    {
        // Mark session as invalid
        await _sessionRepository.TransitionStatusAsync(session.Id, "active", "invalid", ct);

        // Mark cookie as offline in common pool
        if (session.Cookie is not null)
        {
            await _cookiePool.MarkOfflineAsync(session.Cookie, ct);
        }

        // Publish invalid event
        var cookieId = session.CookieId ?? session.Id.ToString();
        try
        {
            await _eventPublisher.PublishAsync(cookieId, "refresh_nav_missing", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish invalid event for session {SessionId}", session.Id);
        }

        // Clean up browser context
        await _browserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);
    }
}
