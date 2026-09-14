using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Weibo;

public interface IWeiboApiClient
{
    Task<WeiboTimelinePage> GetUserTimelineAsync(long uid, string? sinceId = null, CancellationToken ct = default);
}

public sealed class WeiboApiClient : IWeiboApiClient
{
    private const string WeiboHome = "https://m.weibo.cn";

    private readonly IWeiboCookieSource _cookieSource;
    private readonly WeiboCrawlerOptions _options;
    private readonly WeiboRiskControlClassifier _classifier;
    private readonly ILogger<WeiboApiClient> _logger;
    private readonly HttpClient _http;
    private readonly ICookieServiceClient? _cookieServiceClient;

    public WeiboApiClient(
        IWeiboCookieSource cookieSource,
        WeiboCrawlerOptions options,
        ILogger<WeiboApiClient> logger,
        WeiboRiskControlClassifier? classifier = null,
        HttpMessageHandler? handler = null,
        ICookieServiceClient? cookieServiceClient = null)
    {
        _cookieSource = cookieSource;
        _options = options;
        _classifier = classifier ?? new WeiboRiskControlClassifier();
        _logger = logger;
        _cookieServiceClient = cookieServiceClient;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds) };
    }

    public async Task<WeiboTimelinePage> GetUserTimelineAsync(long uid, string? sinceId = null, CancellationToken ct = default)
    {
        // 登录 cookie 的 per-cookie 锁在 lease 释放前保持，覆盖整个 HTTP 调用。
        await using var lease = await _cookieSource.AcquireAsync(ct);

        var path = BuildGetIndexPath(uid, sinceId);

        // 登录态请求经 cookie 服务代理（页面上下文 XHR）；匿名访客路径维持既有直连构造。
        if (lease.IsLoginCookie && !string.IsNullOrEmpty(lease.CookieId) && _cookieServiceClient is not null)
        {
            return await GetUserTimelineViaProxyAsync(uid, path, lease, ct);
        }

        return await GetUserTimelineDirectAsync(uid, path, lease, ct);
    }

    private static string BuildGetIndexPath(long uid, string? sinceId)
    {
        var path = $"/api/container/getIndex?containerid=107603{uid}";
        if (!string.IsNullOrEmpty(sinceId))
        {
            path += $"&since_id={Uri.EscapeDataString(sinceId)}";
        }
        path += $"&.rnd={Uri.EscapeDataString(DateTimeOffset.Now.ToString())}";
        return path;
    }

    private async Task<WeiboTimelinePage> GetUserTimelineViaProxyAsync(long uid, string path, WeiboCookieLease lease, CancellationToken ct)
    {
        _logger.LogInformation("Requesting weibo getIndex via cookie proxy for uid {Uid}", uid);

        var result = await _cookieServiceClient!.ProxyWeiboAsync(lease.CookieId!, path, ct);

        return result.Status switch
        {
            WeiboProxyStatus.Ok => ProcessResponse((HttpStatusCode)result.StatusCode, null, result.Body, null, lease, uid),
            WeiboProxyStatus.SessionInvalid => throw new WeiboProxySessionInvalidException(
                $"weibo proxy reported session invalid for uid {uid}.")
            {
                CookieId = lease.CookieId,
            },
            WeiboProxyStatus.NotFound => throw new WeiboProxySessionInvalidException(
                $"weibo proxy reported cookie/session not found for uid {uid}.")
            {
                CookieId = lease.CookieId,
            },
            WeiboProxyStatus.Timeout => throw new WeiboProxyUnavailableException(
                $"weibo proxy timed out for uid {uid}.", timedOut: true)
            {
                CookieId = lease.CookieId,
            },
            _ => throw new WeiboProxyUnavailableException(
                $"weibo proxy failed for uid {uid}.", timedOut: false)
            {
                CookieId = lease.CookieId,
            },
        };
    }

    private async Task<WeiboTimelinePage> GetUserTimelineDirectAsync(long uid, string path, WeiboCookieLease lease, CancellationToken ct)
    {
        var url = $"{WeiboHome}{path}";
        _logger.LogInformation("Requesting url: {}", url);
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

        return ProcessResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, location, lease, uid);
    }

    private WeiboTimelinePage ProcessResponse(
        HttpStatusCode statusCode,
        string? contentType,
        string? body,
        string? location,
        WeiboCookieLease lease,
        long uid)
    {
        if (_classifier.IsRiskControl(statusCode, contentType, body, location))
        {
            throw new WeiboRiskControlException(
                $"weibo getIndex risk control detected for uid {uid} (status {(int)statusCode}).")
            {
                CookieId = lease.CookieId,
                IsLoginCookie = lease.IsLoginCookie,
            };
        }

        if ((int)statusCode is < 200 or >= 300)
        {
            _logger.LogWarning("weibo getIndex returned HTTP {Status} for uid {Uid}; treating as empty page.",
                (int)statusCode, uid);
            return new WeiboTimelinePage();
        }

        try
        {
            using var doc = JsonDocument.Parse(body ?? string.Empty);
            return WeiboMblogParser.ParsePage(doc.RootElement);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "weibo getIndex returned unparseable JSON for uid {Uid}; treating as empty page.", uid);
            return new WeiboTimelinePage();
        }
    }
}
