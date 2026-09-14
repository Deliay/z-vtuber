using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Cookie.Browser;

public class LoginSessionWorker : BackgroundService
{
    private readonly CookiePlatformRegistry _registry;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<LoginSessionWorker> _logger;

    public LoginSessionWorker(
        CookiePlatformRegistry registry,
        IDistributedLockProvider lockProvider,
        IOptions<CookieServiceOptions> options,
        ILogger<LoginSessionWorker> logger)
    {
        _registry = registry;
        _lockProvider = lockProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LoginSessionWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var context in _registry.All)
            {
                try
                {
                    await ProcessPendingSessions(context, stoppingToken);
                    await ProcessExpiredSessions(context, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in LoginSessionWorker loop for platform {Platform}", context.Platform);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPendingSessions(CookiePlatformContext context, CancellationToken ct)
    {
        var pendingSessions = await context.SessionRepository.GetByStatusAsync("pending_login", ct);

        foreach (var session in pendingSessions)
        {
            if (session.ExpiresAt <= DateTime.UtcNow)
            {
                continue;
            }

            var lockKey = $"lock:cookie-login:{context.Profile.LockKeyPrefix}{session.Id}";
            var lockTtl = TimeSpan.FromSeconds(30);
            var lockWait = TimeSpan.FromMilliseconds(500);

            await using var lockHandle = await _lockProvider.AcquireAsync(lockKey, lockTtl, lockWait, ct);
            if (lockHandle is null)
            {
                continue;
            }

            try
            {
                await MonitorLoginAsync(context, session, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring login for session {SessionId}", session.Id);
            }
        }
    }

    private async Task MonitorLoginAsync(CookiePlatformContext context, CookieSessionRecord session, CancellationToken ct)
    {
        var remaining = session.ExpiresAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        var navResponse = await context.BrowserSessionManager.WaitForNavResponseAsync(
            session.Id.ToString(), remaining, ct);

        if (navResponse is null)
        {
            _logger.LogInformation("Login timeout for session {SessionId}", session.Id);
            return;
        }

        var cookie = await context.BrowserSessionManager.ExtractCookiesAsync(session.Id.ToString(), ct);

        var cookieRecord = await context.CookiePool.AddAsync(cookie, accountId: null, ct: ct);

        var identity = context.Profile.ExtractIdentity(navResponse);

        var migrated = await context.SessionRepository.TransitionStatusAsync(
            session.Id, "pending_login", "active", ct);

        if (migrated)
        {
            session.Status = "active";
            session.Cookie = cookie;
            session.CookieId = cookieRecord.Id.ToString();
            session.LastLoginAt = DateTime.UtcNow;
            session.Mid = identity.Mid;
            session.Uname = identity.Uname;
            await context.SessionRepository.UpdateAsync(session, ct);

            var update = Builders<CookieRecord>.Update.Set(c => c.AccountId, session.Id.ToString());
            await context.CookiePool.UpdateAsync(cookieRecord.Id.ToString(), update, ct);

            _logger.LogInformation("Login successful for {Platform} session {SessionId}, cookie created {CookieId}, mid {Mid}",
                context.Platform, session.Id, cookieRecord.Id, identity.Mid);
        }
    }

    private async Task ProcessExpiredSessions(CookiePlatformContext context, CancellationToken ct)
    {
        var expiredSessions = await context.SessionRepository.GetPendingExpiredAsync(DateTime.UtcNow, ct);

        foreach (var session in expiredSessions)
        {
            var migrated = await context.SessionRepository.TransitionStatusAsync(
                session.Id, "pending_login", "expired", ct);

            if (migrated)
            {
                _logger.LogInformation("Session {SessionId} expired, cleaning up browser context", session.Id);
                await context.BrowserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);
            }
        }
    }
}
