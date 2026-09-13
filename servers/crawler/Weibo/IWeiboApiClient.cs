using System.Text.Json;
using Microsoft.Extensions.Logging;
using Z.Vtuber.Crawler.Configuration;

namespace Z.Vtuber.Crawler.Weibo;

public interface IWeiboApiClient
{
    Task<WeiboTimelinePage> GetUserTimelineAsync(long uid, string? sinceId = null, CancellationToken ct = default);
}

public sealed class WeiboApiClient : IWeiboApiClient
{
    private const string GetIndexUrl = "https://m.weibo.cn/api/container/getIndex";

    private readonly IWeiboCookieSource _cookieSource;
    private readonly WeiboCrawlerOptions _options;
    private readonly WeiboRiskControlClassifier _classifier;
    private readonly ILogger<WeiboApiClient> _logger;
    private readonly HttpClient _http;

    public WeiboApiClient(
        IWeiboCookieSource cookieSource,
        WeiboCrawlerOptions options,
        ILogger<WeiboApiClient> logger,
        WeiboRiskControlClassifier? classifier = null,
        HttpMessageHandler? handler = null)
    {
        _cookieSource = cookieSource;
        _options = options;
        _classifier = classifier ?? new WeiboRiskControlClassifier();
        _logger = logger;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds) };
    }

    public async Task<WeiboTimelinePage> GetUserTimelineAsync(long uid, string? sinceId = null, CancellationToken ct = default)
    {
        // 登录 cookie 的 per-cookie 锁在 lease 释放前保持，覆盖整个 HTTP 调用。
        await using var lease = await _cookieSource.AcquireAsync(ct);

        var url = $"{GetIndexUrl}?containerid=107603{uid}";
        if (!string.IsNullOrEmpty(sinceId))
        {
            url += $"&since_id={Uri.EscapeDataString(sinceId)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", $"https://m.weibo.cn/u/{uid}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("mweibo-pwa", "1");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"iOS\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?1");
        request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        if (!string.IsNullOrEmpty(lease.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", lease.Cookie);
        }

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var location = response.Headers.Location?.ToString();

        if (_classifier.IsRiskControl(response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, location))
        {
            throw new WeiboRiskControlException(
                $"weibo getIndex risk control detected for uid {uid} (status {(int)response.StatusCode}).")
            {
                CookieId = lease.CookieId,
                IsLoginCookie = lease.IsLoginCookie,
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("weibo getIndex returned HTTP {(int)StatusCode} for uid {Uid}; treating as empty page.",
                (int)response.StatusCode, uid);
            return new WeiboTimelinePage();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return WeiboMblogParser.ParsePage(doc.RootElement);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "weibo getIndex returned unparseable JSON for uid {Uid}; treating as empty page.", uid);
            return new WeiboTimelinePage();
        }
    }
}
