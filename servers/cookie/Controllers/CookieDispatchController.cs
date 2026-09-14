using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Pool;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Cookie.Controllers;

[ApiController]
[Route("api/cookie")]
public class CookieDispatchController : ControllerBase
{
    private readonly ICookieDispatchService _dispatchService;
    private readonly IInvalidationService _invalidationService;
    private readonly ILogger<CookieDispatchController> _logger;

    public CookieDispatchController(
        ICookieDispatchService dispatchService,
        IInvalidationService invalidationService,
        ILogger<CookieDispatchController> logger)
    {
        _dispatchService = dispatchService;
        _invalidationService = invalidationService;
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
}

public class CookieCheckoutResponse
{
    public string CookieId { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string? Cookie { get; set; }
    public bool Refreshed { get; set; }
}

public class ReportInvalidRequest
{
    public string CookieId { get; set; } = string.Empty;
}

public class CooldownRequest
{
    public string CookieId { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
}

public class ProxyWeiboRequest
{
    public string CookieId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public class ProxyWeiboResponse
{
    public int Status { get; set; }
    public string Body { get; set; } = string.Empty;
}
