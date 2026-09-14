using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Cookie.Refresh;

public class SessionRefreshJob : BackgroundService
{
    private readonly CookiePlatformRegistry _registry;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<SessionRefreshJob> _logger;

    /// <summary>DI 构造：覆盖全部平台。</summary>
    public SessionRefreshJob(
        CookiePlatformRegistry registry,
        IDistributedLockProvider lockProvider,
        IOptions<CookieServiceOptions> options,
        ILogger<SessionRefreshJob> logger)
    {
        _registry = registry;
        _lockProvider = lockProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>单平台兼容构造（测试用）。</summary>
    public SessionRefreshJob(
        ICookieSessionRepository sessionRepository,
        Z.Vtuber.Cookie.Browser.IBrowserSessionManager browserSessionManager,
        ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        ICookieInvalidEventPublisher eventPublisher,
        IOptions<CookieServiceOptions> options,
        ILogger<SessionRefreshJob> logger)
        : this(
            new CookiePlatformRegistry(new CookiePlatformContext
            {
                Profile = new BilibiliCookiePlatformProfile(),
                SessionRepository = sessionRepository,
                CookiePool = cookiePool,
                BrowserSessionManager = browserSessionManager,
                EventPublisher = eventPublisher,
            }),
            lockProvider,
            options,
            logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionRefreshJob started");

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var context in _registry.All)
            {
                try
                {
                    await RefreshActiveSessionsForPlatformAsync(context, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SessionRefreshJob loop for platform {Platform}", context.Platform);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task RefreshActiveSessions(CancellationToken ct)
    {
        foreach (var context in _registry.All)
        {
            await RefreshActiveSessionsForPlatformAsync(context, ct);
        }
    }

    private async Task RefreshActiveSessionsForPlatformAsync(CookiePlatformContext context, CancellationToken ct)
    {
        var activeSessions = await context.SessionRepository.GetByStatusAsync("active", ct);
        var refreshThreshold = DateTime.UtcNow.AddMinutes(-_options.RefreshIntervalMinutes);

        foreach (var session in activeSessions)
        {
            if (session.LastRefreshAt > refreshThreshold)
            {
                continue;
            }

            var lockKey = $"lock:cookie-refresh:{context.Profile.LockKeyPrefix}{session.Id}";
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
                await RefreshSessionForPlatformAsync(context, session, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing session {SessionId}", session.Id);
            }
        }
    }

    // 单平台反射测试兼容入口：作用于首个注册平台。
    private Task RefreshSessionAsync(CookieSessionRecord session, CancellationToken ct)
        => RefreshSessionForPlatformAsync(_registry.All[0], session, ct);

    private async Task RefreshSessionForPlatformAsync(CookiePlatformContext context, CookieSessionRecord session, CancellationToken ct)
    {
        try
        {
            await context.BrowserSessionManager.RefreshSessionAsync(session.Id.ToString(), ct);
            var newCookie = await context.BrowserSessionManager.ExtractCookiesAsync(session.Id.ToString(), ct);

            if (!string.IsNullOrEmpty(session.Cookie) && session.Cookie != newCookie)
            {
                await context.CookiePool.MarkOfflineAsync(session.Cookie, ct);
            }

            var newCookieRecord = await context.CookiePool.AddAsync(newCookie, session.Id.ToString(), ct);

            session.Cookie = newCookie;
            session.CookieId = newCookieRecord.Id.ToString();
            session.LastRefreshAt = DateTime.UtcNow;
            session.ConsecutiveRefreshFailures = 0;
            await context.SessionRepository.UpdateAsync(session, ct);

            _logger.LogDebug("Refreshed {Platform} session {SessionId} successfully", context.Platform, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh failed for session {SessionId}", session.Id);

            await context.SessionRepository.IncrementRefreshFailureAsync(session.Id, ct);
            var updatedSession = await context.SessionRepository.GetByIdAsync(session.Id, ct);

            if (updatedSession is not null && updatedSession.ConsecutiveRefreshFailures >= _options.MaxConsecutiveRefreshFailures)
            {
                _logger.LogWarning("Session {SessionId} exceeded max refresh failures, marking invalid", session.Id);
                await InvalidateSessionAsync(context, session, ct);
            }
        }
    }

    private async Task InvalidateSessionAsync(CookiePlatformContext context, CookieSessionRecord session, CancellationToken ct)
    {
        await context.SessionRepository.TransitionStatusAsync(session.Id, "active", "invalid", ct);

        if (session.Cookie is not null)
        {
            await context.CookiePool.MarkOfflineAsync(session.Cookie, ct);
        }

        var cookieId = session.CookieId ?? session.Id.ToString();
        try
        {
            await context.EventPublisher.PublishAsync(cookieId, "refresh_nav_missing", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish invalid event for session {SessionId}", session.Id);
        }

        await context.BrowserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);
    }
}
