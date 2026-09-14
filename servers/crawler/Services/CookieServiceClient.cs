using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Services;

public interface ICookieServiceClient
{
    Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default);
    Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default);
    Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default);

    Task<WeiboCookieCheckoutResult> CheckoutWeiboAsync(CancellationToken ct = default);
    Task<ReportInvalidResult> ReportWeiboInvalidAsync(string cookieId, CancellationToken ct = default);
    Task CooldownWeiboAsync(string cookieId, TimeSpan duration, CancellationToken ct = default);
}

public sealed class CookieServiceClient : ICookieServiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<CookieServiceClient> _logger;

    public CookieServiceClient(HttpClient http, ILogger<CookieServiceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/cookie/checkout", new { }, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NoAvailableCookieException();
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CookieCheckoutResult>(cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Cookie checkout returned null result.");
    }

    public Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default) =>
        ReportInvalidCoreAsync("/api/cookie/report-invalid", cookieId, ct);

    public async Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        var payload = new { CookieId = cookieId, DurationSeconds = (int)duration.TotalSeconds };
        var response = await _http.PostAsJsonAsync("/api/cookie/cooldown", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<WeiboCookieCheckoutResult> CheckoutWeiboAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/cookie/weibo/checkout", new { }, ct);

        // 微博有匿名访客兜底：无可用登录 cookie 不是错误，返回不可用信号交由上层降级。
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new WeiboCookieCheckoutResult { Available = false };
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CookieCheckoutResponse>(cancellationToken: ct);
        if (result is null || string.IsNullOrEmpty(result.Cookie))
        {
            return new WeiboCookieCheckoutResult { Available = false };
        }

        return new WeiboCookieCheckoutResult
        {
            Available = true,
            CookieId = result.CookieId,
            Cookie = result.Cookie,
            AccountId = result.AccountId,
        };
    }

    public Task<ReportInvalidResult> ReportWeiboInvalidAsync(string cookieId, CancellationToken ct = default) =>
        ReportInvalidCoreAsync("/api/cookie/weibo/report-invalid", cookieId, ct);

    public async Task CooldownWeiboAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        var payload = new { CookieId = cookieId, DurationSeconds = (int)duration.TotalSeconds };
        var response = await _http.PostAsJsonAsync("/api/cookie/weibo/cooldown", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<ReportInvalidResult> ReportInvalidCoreAsync(string path, string cookieId, CancellationToken ct)
    {
        var payload = new { CookieId = cookieId };
        var response = await _http.PostAsJsonAsync(path, payload, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<ReportInvalidResponse>(ct);
            if (result is null || string.IsNullOrEmpty(result.Cookie))
            {
                return new ReportInvalidResult
                {
                    Status = ReportInvalidStatus.SessionInvalid,
                };
            }
            return new ReportInvalidResult
            {
                Status = ReportInvalidStatus.Refreshed,
                CookieId = result.CookieId,
                Cookie = result.Cookie,
            };
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            var result = await response.Content.ReadFromJsonAsync<ReportInvalidResponse>(ct);
            return new ReportInvalidResult
            {
                Status = ReportInvalidStatus.SessionInvalid,
                AccountId = result?.AccountId,
            };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ReportInvalidResult
            {
                Status = ReportInvalidStatus.NotFound,
            };
        }

        if (response.StatusCode == HttpStatusCode.GatewayTimeout)
        {
            return new ReportInvalidResult
            {
                Status = ReportInvalidStatus.Timeout,
            };
        }

        response.EnsureSuccessStatusCode();
        return new ReportInvalidResult { Status = ReportInvalidStatus.NotFound };
    }
}

public sealed class CookieCheckoutResult
{
    public string CookieId { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
}

/// <summary>微博 checkout 结果。区别于 B 站：无可用登录 cookie 时 <see cref="Available"/> 为 false（有匿名兜底），不抛异常。</summary>
public sealed class WeiboCookieCheckoutResult
{
    public bool Available { get; init; }
    public string? CookieId { get; init; }
    public string? Cookie { get; init; }
    public string? AccountId { get; init; }
}

public sealed class ReportInvalidResult
{
    public ReportInvalidStatus Status { get; set; }
    public string? CookieId { get; set; }
    public string? Cookie { get; set; }
    public string? AccountId { get; set; }
}

public enum ReportInvalidStatus
{
    Refreshed,
    SessionInvalid,
    NotFound,
    Timeout,
}

internal sealed class ReportInvalidResponse
{
    public string? CookieId { get; set; }
    public string? AccountId { get; set; }
    public string? Cookie { get; set; }
}

internal sealed class CookieCheckoutResponse
{
    public string? CookieId { get; set; }
    public string? AccountId { get; set; }
    public string? Cookie { get; set; }
}
