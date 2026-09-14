using System.Net;
using System.Net.Http.Json;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Recording.Services;

public sealed class CookieCheckout
{
    public string CookieId { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
}

public interface IRecordingCookieClient
{
    Task<CookieCheckout> CheckoutAsync(CancellationToken ct = default);
    Task ReportInvalidAsync(string cookieId, CancellationToken ct = default);
}

/// <summary>
/// recording 侧精简版 Cookie 客户端：仅 checkout + report-invalid（复用 cookie 服务既有 HTTP API）。
/// </summary>
public sealed class CookieServiceClient(HttpClient http) : IRecordingCookieClient
{
    public async Task<CookieCheckout> CheckoutAsync(CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/api/cookie/checkout", new { }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NoAvailableCookieException();
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CookieCheckoutResponse>(cancellationToken: ct);
        return new CookieCheckout
        {
            CookieId = result?.CookieId ?? string.Empty,
            Cookie = result?.Cookie ?? string.Empty,
        };
    }

    public async Task ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/api/cookie/report-invalid", new { CookieId = cookieId }, ct);

        // 404/410/504 均视为已上报，无需异常
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.GatewayTimeout)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class CookieCheckoutResponse
    {
        public string? CookieId { get; set; }
        public string? Cookie { get; set; }
    }
}
