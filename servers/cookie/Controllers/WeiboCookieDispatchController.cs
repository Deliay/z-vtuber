using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;
using Z.Vtuber.Cookie.Pool;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Cookie.Controllers;

[ApiController]
[Route("api/cookie/weibo")]
public class WeiboCookieDispatchController : ControllerBase
{
    private const string Platform = "weibo";

    private readonly ICookieDispatchService _dispatchService;
    private readonly IInvalidationService _invalidationService;
    private readonly ICookiePoolService _cookiePool;
    private readonly ICookieSessionRepository _sessionRepository;
    private readonly IBrowserSessionManager _browserSessionManager;
    private readonly ICookiePlatformProfile _profile;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<WeiboCookieDispatchController> _logger;

    public WeiboCookieDispatchController(
        [FromKeyedServices(Platform)] ICookieDispatchService dispatchService,
        [FromKeyedServices(Platform)] IInvalidationService invalidationService,
        [FromKeyedServices(Platform)] ICookiePoolService cookiePool,
        [FromKeyedServices(Platform)] ICookieSessionRepository sessionRepository,
        [FromKeyedServices(Platform)] IBrowserSessionManager browserSessionManager,
        [FromKeyedServices(Platform)] ICookiePlatformProfile profile,
        IDistributedLockProvider lockProvider,
        IOptions<CookieServiceOptions> options,
        ILogger<WeiboCookieDispatchController> logger)
    {
        _dispatchService = dispatchService;
        _invalidationService = invalidationService;
        _cookiePool = cookiePool;
        _sessionRepository = sessionRepository;
        _browserSessionManager = browserSessionManager;
        _profile = profile;
        _lockProvider = lockProvider;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("available")]
    public async Task<IActionResult> ListAvailable(CancellationToken ct)
    {
        var items = await _dispatchService.GetAvailableAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CancellationToken ct)
    {
        try
        {
            var result = await _dispatchService.CheckoutAsync(ct);
            return Ok(new CookieCheckoutResponse
            {
                CookieId = result.CookieId,
                AccountId = result.AccountId,
                Cookie = result.Cookie,
            });
        }
        catch (NoAvailableCookieException)
        {
            return NotFound(new { error = "no_available_cookie" });
        }
    }

    [HttpPost("report-invalid")]
    public async Task<IActionResult> ReportInvalid([FromBody] ReportInvalidRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.CookieId))
        {
            return BadRequest(new { error = "cookieId is required" });
        }

        var result = await _invalidationService.ReportInvalidAsync(request.CookieId, ct);

        return result.Result switch
        {
            InvalidationResult.NotFound => NotFound(new { error = "cookie_not_found" }),
            InvalidationResult.SessionInvalid => StatusCode(410, new
            {
                cookieId = request.CookieId,
                accountId = result.AccountId,
                reason = "session_invalid",
            }),
            InvalidationResult.Timeout => StatusCode(504, new { error = "timeout" }),
            _ => Ok(new CookieCheckoutResponse
            {
                CookieId = result.NewCookieId ?? request.CookieId,
                AccountId = result.AccountId,
                Cookie = result.NewCookie,
                Refreshed = true,
            }),
        };
    }

    [HttpPost("cooldown")]
    public async Task<IActionResult> SetCooldown([FromBody] CooldownRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.CookieId))
        {
            return BadRequest(new { error = "cookieId is required" });
        }

        if (request.DurationSeconds < 1)
        {
            return BadRequest(new { error = "durationSeconds must be >= 1" });
        }

        var modified = await _dispatchService.SetCooldownAsync(
            request.CookieId,
            TimeSpan.FromSeconds(request.DurationSeconds),
            ct);

        if (!modified)
        {
            return NotFound(new { error = "cookie_not_found" });
        }

        return NoContent();
    }

    [HttpPost("proxy")]
    public async Task<IActionResult> Proxy([FromBody] ProxyWeiboRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.CookieId) || string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new { error = "cookieId and path are required" });
        }

        if (!IsAllowedProxyPath(request.Path))
        {
            _logger.LogWarning("Rejected non-whitelisted weibo proxy target");
            return BadRequest(new { error = "invalid_proxy_target" });
        }

        var cookieRecord = await _cookiePool.FindByCookieIdAsync(request.CookieId, ct);
        if (cookieRecord is null || cookieRecord.Status != CookieStatus.Active)
        {
            return NotFound(new { error = "cookie_not_found" });
        }

        var session = await _sessionRepository.GetByCookieIdAsync(request.CookieId, ct);
        if (session is null || session.Status != "active")
        {
            return StatusCode(410, new
            {
                cookieId = request.CookieId,
                accountId = session?.Id.ToString() ?? cookieRecord.AccountId,
                reason = "session_invalid",
            });
        }

        var lockKey = $"lock:cookie-refresh:{_profile.LockKeyPrefix}{session.Id}";
        var lockTtl = TimeSpan.FromSeconds(_options.RefreshLockTtlSeconds);
        var lockWait = TimeSpan.FromMilliseconds(_options.LockAcquireWaitMs);

        await using var lockHandle = await _lockProvider.AcquireAsync(lockKey, lockTtl, lockWait, ct);
        if (lockHandle is null)
        {
            _logger.LogWarning("Could not acquire proxy lock for session {SessionId}", session.Id);
            return StatusCode(504, new { error = "timeout" });
        }

        try
        {
            var result = await _browserSessionManager.ProxyRequestAsync(session.Id.ToString(), request.Path, ct);
            return Ok(new ProxyWeiboResponse { Status = result.Status, Body = result.Body });
        }
        catch (TimeoutException)
        {
            return StatusCode(504, new { error = "timeout" });
        }
        catch (BrowserProxyException ex)
        {
            _logger.LogWarning(ex, "Browser proxy request failed for session {SessionId}", session.Id);
            return StatusCode(502, new { error = "browser_proxy_failed" });
        }
    }

    private static bool IsAllowedProxyPath(string path)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
