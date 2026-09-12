using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Persistence;
using MongoDB.Bson;

namespace Z.Vtuber.Cookie.Browser;

public class LoginSessionWorker : BackgroundService
{
    private readonly IBrowserSessionManager _browserSessionManager;
    private readonly ICookieSessionRepository _sessionRepository;
    private readonly ICookiePoolService _cookiePool;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<LoginSessionWorker> _logger;

    public LoginSessionWorker(
        IBrowserSessionManager browserSessionManager,
        ICookieSessionRepository sessionRepository,
        ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        IOptions<CookieServiceOptions> options,
        ILogger<LoginSessionWorker> logger)
    {
        _browserSessionManager = browserSessionManager;
        _sessionRepository = sessionRepository;
        _cookiePool = cookiePool;
        _lockProvider = lockProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LoginSessionWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingSessions(stoppingToken);
                await ProcessExpiredSessions(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LoginSessionWorker loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPendingSessions(CancellationToken ct)
    {
        var pendingSessions = await _sessionRepository.GetByStatusAsync("pending_login", ct);

        foreach (var session in pendingSessions)
        {
            if (session.ExpiresAt <= DateTime.UtcNow)
            {
                continue;
            }

            var lockKey = $"lock:cookie-login:{session.Id}";
            var lockTtl = TimeSpan.FromSeconds(30);
            var lockWait = TimeSpan.FromMilliseconds(500);

            await using var lockHandle = await _lockProvider.AcquireAsync(lockKey, lockTtl, lockWait, ct);
            if (lockHandle is null)
            {
                continue;
            }

            try
            {
                await MonitorLoginAsync(session, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring login for session {SessionId}", session.Id);
            }
        }
    }

    private async Task MonitorLoginAsync(CookieSessionRecord session, CancellationToken ct)
    {
        var remaining = session.ExpiresAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        var navResponse = await _browserSessionManager.WaitForNavResponseAsync(
            session.Id.ToString(), remaining, ct);

        if (navResponse is null)
        {
            _logger.LogInformation("Login timeout for session {SessionId}", session.Id);
            return;
        }

        // Login successful - extract cookies
        var cookie = await _browserSessionManager.ExtractCookiesAsync(session.Id.ToString(), ct);

        // Create cookie record in common pool
        var cookieRecord = await _cookiePool.AddAsync(cookie, ct);

        // Transition session to active
        var migrated = await _sessionRepository.TransitionStatusAsync(
            session.Id, "pending_login", "active", ct);

        if (migrated)
        {
            session.Status = "active";
            session.Cookie = cookie;
            session.CookieId = cookieRecord.Id.ToString();
            session.LastLoginAt = DateTime.UtcNow;
            session.Mid = cookieRecord.Id.GetHashCode(); // Will be updated by nav probe
            await _sessionRepository.UpdateAsync(session, ct);

            _logger.LogInformation("Login successful for session {SessionId}, cookie created {CookieId}",
                session.Id, cookieRecord.Id);
        }
    }

    private async Task ProcessExpiredSessions(CancellationToken ct)
    {
        var expiredSessions = await _sessionRepository.GetPendingExpiredAsync(DateTime.UtcNow, ct);

        foreach (var session in expiredSessions)
        {
            var migrated = await _sessionRepository.TransitionStatusAsync(
                session.Id, "pending_login", "expired", ct);

            if (migrated)
            {
                _logger.LogInformation("Session {SessionId} expired, cleaning up browser context", session.Id);
                await _browserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);
            }
        }
    }
}
