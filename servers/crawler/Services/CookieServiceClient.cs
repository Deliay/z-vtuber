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

    public async Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        var payload = new { CookieId = cookieId };
        var response = await _http.PostAsJsonAsync("/api/cookie/report-invalid", payload, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<ReportInvalidResponse>(ct);
            return new ReportInvalidResult
            {
                Status = ReportInvalidStatus.Refreshed,
                CookieId = result!.CookieId,
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

    public async Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        var payload = new { CookieId = cookieId, DurationSeconds = (int)duration.TotalSeconds };
        var response = await _http.PostAsJsonAsync("/api/cookie/cooldown", payload, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class CookieCheckoutResult
{
    public string CookieId { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
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
