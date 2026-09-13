using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Cookie.Controllers;

[ApiController]
[Route("api/cookie/weibo")]
public class WeiboCookieAccountController : ControllerBase
{
    private const string Platform = "weibo";

    private readonly ICookieSessionRepository _sessionRepository;
    private readonly IBrowserSessionManager _browserSessionManager;
    private readonly ICookiePoolService _cookiePool;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly ICookieInvalidEventPublisher _eventPublisher;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<WeiboCookieAccountController> _logger;

    public WeiboCookieAccountController(
        [FromKeyedServices(Platform)] ICookieSessionRepository sessionRepository,
        [FromKeyedServices(Platform)] IBrowserSessionManager browserSessionManager,
        [FromKeyedServices(Platform)] ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        [FromKeyedServices(Platform)] ICookieInvalidEventPublisher eventPublisher,
        IOptions<CookieServiceOptions> options,
        ILogger<WeiboCookieAccountController> logger)
    {
        _sessionRepository = sessionRepository;
        _browserSessionManager = browserSessionManager;
        _cookiePool = cookiePool;
        _lockProvider = lockProvider;
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var sessions = await _sessionRepository.GetByStatusAsync("pending_login", ct);
        var activeSessions = await _sessionRepository.GetByStatusAsync("active", ct);
        var expiredSessions = await _sessionRepository.GetByStatusAsync("expired", ct);
        var invalidSessions = await _sessionRepository.GetByStatusAsync("invalid", ct);

        var allSessions = sessions.Concat(activeSessions).Concat(expiredSessions).Concat(invalidSessions);

        var items = allSessions.Select(s => new AccountItem
        {
            AccountId = s.Id.ToString(),
            Platform = Platform,
            Status = s.Status,
            Mid = s.Mid,
            Uname = s.Uname,
            CookieId = s.CookieId,
            CookieStatus = s.CookieId != null ? "active" : null,
            LastRefreshAt = s.LastRefreshAt,
            LastLoginAt = s.LastLoginAt,
            CreatedAt = s.CreatedAt,
        }).ToList();

        return Ok(new { items });
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount(CancellationToken ct)
    {
        var pendingSessions = await _sessionRepository.GetByStatusAsync("pending_login", ct);
        if (pendingSessions.Count >= _options.MaxPendingLoginSessions)
        {
            return StatusCode(429, new { error = "Too many pending login sessions" });
        }

        var expiresAt = DateTime.UtcNow.AddSeconds(_options.QrCodeExpiresSeconds);

        var session = new CookieSessionRecord
        {
            Status = "pending_login",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };

        await _sessionRepository.InsertAsync(session, ct);

        var accountId = session.Id.ToString();

        try
        {
            var result = await _browserSessionManager.CreateLoginSessionAsync(accountId, ct);

            session.QrCodeImage = result.QrCodeImage;
            session.ExpiresAt = result.ExpiresAt;
            session.UserDataDir = Path.Combine(_options.BrowserSessionDataDir, accountId);
            await _sessionRepository.UpdateAsync(session, ct);

            return StatusCode(202, new PendingAccountResponse
            {
                AccountId = accountId,
                Status = "pending_login",
                QrCodeImage = result.QrCodeImage,
                ExpiresAt = result.ExpiresAt,
            });
        }
        catch (Exception ex)
        {
            await _sessionRepository.DeleteAsync(session.Id, ct);
            _logger.LogError(ex, "Failed to create weibo login session");
            return StatusCode(500, new { error = "Failed to create login session" });
        }
    }

    [HttpGet("accounts/{accountId}")]
    public async Task<IActionResult> GetAccount(string accountId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var objectId))
        {
            return NotFound(new { error = "Account not found" });
        }

        var session = await _sessionRepository.GetByIdAsync(objectId, ct);
        if (session is null)
        {
            return NotFound(new { error = "Account not found" });
        }

        return Ok(new AccountStatusResponse
        {
            AccountId = session.Id.ToString(),
            Status = session.Status,
            Mid = session.Mid,
            Uname = session.Uname,
            QrCodeImage = session.Status == "pending_login" ? session.QrCodeImage : null,
            ExpiresAt = session.ExpiresAt,
        });
    }

    [HttpDelete("accounts/{accountId}")]
    public async Task<IActionResult> DeleteAccount(string accountId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var objectId))
        {
            return NoContent();
        }

        var session = await _sessionRepository.GetByIdAsync(objectId, ct);
        if (session is not null)
        {
            if (!string.IsNullOrEmpty(session.CookieId))
            {
                await _cookiePool.MarkOfflineAsync(session.CookieId, ct);
            }

            await _browserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);
            await _sessionRepository.DeleteAsync(objectId, ct);

            try
            {
                await _eventPublisher.PublishAsync(session.CookieId ?? accountId, "account_removed", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish account_removed event for weibo account {AccountId}", accountId);
            }
        }

        return NoContent();
    }
}
