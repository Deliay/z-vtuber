using System.Net.Http.Json;
using System.Text.Json;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Services;

public sealed class CookieServiceClient
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

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new NoAvailableCookieException();
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CookieCheckoutResult>(cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Cookie checkout returned null result.");
    }

    public async Task ReportInvalidAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        var payload = new { CookieId = cookieId, Reason = reason };
        var response = await _http.PostAsJsonAsync("/api/cookie/report-invalid", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        var payload = new { CookieId = cookieId, DurationMinutes = (int)duration.TotalMinutes };
        var response = await _http.PostAsJsonAsync("/api/cookie/cooldown", payload, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class CookieCheckoutResult
{
    public string CookieId { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
}
