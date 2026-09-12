using Microsoft.AspNetCore.Mvc;
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
[Route("api/cookie")]
public class CookieAccountController : ControllerBase
{
    private readonly ICookieSessionRepository _sessionRepository;
    private readonly IBrowserSessionManager _browserSessionManager;
    private readonly ICookiePoolService _cookiePool;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly ICookieInvalidEventPublisher _eventPublisher;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<CookieAccountController> _logger;

    public CookieAccountController(
        ICookieSessionRepository sessionRepository,
        IBrowserSessionManager browserSessionManager,
        ICookiePoolService cookiePool,
        IDistributedLockProvider lockProvider,
        ICookieInvalidEventPublisher eventPublisher,
        IOptions<CookieServiceOptions> options,
        ILogger<CookieAccountController> logger)
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
        // Check concurrent login limit
        var pendingSessions = await _sessionRepository.GetByStatusAsync("pending_login", ct);
        if (pendingSessions.Count >= _options.MaxPendingLoginSessions)
        {
            return StatusCode(429, new { error = "Too many pending login sessions" });
        }

        try
        {
            var expiresAt = DateTime.UtcNow.AddSeconds(_options.QrCodeExpiresSeconds);

            var session = new CookieSessionRecord
            {
                Status = "pending_login",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
            };

            await _sessionRepository.InsertAsync(session, ct);

            var accountId = session.Id.ToString();

            var result = await _browserSessionManager.CreateLoginSessionAsync(accountId, ct);

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
            _logger.LogError(ex, "Failed to create login session");
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
            // Offline cookie if exists
            if (!string.IsNullOrEmpty(session.CookieId))
            {
                await _cookiePool.MarkOfflineAsync(session.CookieId, ct);
            }

            await _browserSessionManager.DeleteSessionAsync(session.Id.ToString(), ct);
            await _sessionRepository.DeleteAsync(objectId, ct);

            // Publish account_removed event
            try
            {
                await _eventPublisher.PublishAsync(session.CookieId ?? accountId, "account_removed", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish account_removed event for account {AccountId}", accountId);
            }
        }

        return NoContent();
    }
}

public class AccountItem
{
    public string AccountId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? Mid { get; set; }
    public string? Uname { get; set; }
    public string? CookieId { get; set; }
    public string? CookieStatus { get; set; }
    public DateTime LastRefreshAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PendingAccountResponse
{
    public string AccountId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending_login";
    public string QrCodeImage { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class AccountStatusResponse
{
    public string AccountId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? Mid { get; set; }
    public string? Uname { get; set; }
    public string? QrCodeImage { get; set; }
    public DateTime ExpiresAt { get; set; }
}
