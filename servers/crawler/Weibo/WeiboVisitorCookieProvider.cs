using System.Text.Json;
using Microsoft.Extensions.Logging;
using Z.Vtuber.Crawler.Configuration;

namespace Z.Vtuber.Crawler.Weibo;

public interface IWeiboVisitorCookieProvider
{
    /// <summary>返回可直接作为 Cookie 请求头的访客凭证（SUB/SUBP），必要时自动申请或刷新。</summary>
    Task<string> GetOrRefreshAsync(CancellationToken ct = default);

    /// <summary>作废当前凭证并立即重建一份（供风控/失效后恢复）。</summary>
    Task RenewAsync(CancellationToken ct = default);
}

public sealed class WeiboVisitorCookieProvider : IWeiboVisitorCookieProvider
{
    private const string GenVisitor2Url = "https://visitor.passport.weibo.cn/visitor/genvisitor2";
    private const string GenVisitorUrl = "https://passport.weibo.com/visitor/genvisitor";
    private const string IncarnateUrl = "https://passport.weibo.com/visitor/visitor";
    private const string CrossDomainUrl = "https://login.sina.com.cn/visitor/visitor";

    private readonly WeiboCrawlerOptions _options;
    private readonly ILogger<WeiboVisitorCookieProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cookieHeader;
    private DateTimeOffset? _lastAcquiredUtc;

    public WeiboVisitorCookieProvider(
        WeiboCrawlerOptions options,
        ILogger<WeiboVisitorCookieProvider> logger,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds) };
    }

    public async Task<string> GetOrRefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_cookieHeader) && IsFresh())
            {
                return _cookieHeader;
            }

            await AcquireAsync(ct);
            return _cookieHeader ?? string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenewAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _cookieHeader = null;
            _lastAcquiredUtc = null;
            await AcquireAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh()
    {
        if (_lastAcquiredUtc is null)
        {
            return false;
        }

        var elapsed = _timeProvider.GetUtcNow() - _lastAcquiredUtc.Value;
        return elapsed < TimeSpan.FromMinutes(_options.CookieRefreshIntervalMinutes);
    }

    private async Task AcquireAsync(CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var header = await TryOneStepAsync(ct) ?? await TryFallbackAsync(ct);
                if (!string.IsNullOrEmpty(header))
                {
                    _cookieHeader = header;
                    _lastAcquiredUtc = _timeProvider.GetUtcNow();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Weibo visitor cookie acquisition attempt {Attempt} failed.", attempt);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        _logger.LogWarning("Weibo visitor cookie acquisition exhausted all attempts.");
    }

    // 主路径：genvisitor2 一步申请，返回 JSONP，取 data.sub/data.subp
    private async Task<string?> TryOneStepAsync(CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["cb"] = "visitor_callback",
            ["from"] = "weibo",
            ["tid"] = "",
            ["return_url"] = "https://m.weibo.cn/",
        });
        form.Headers.TryAddWithoutValidation("Referer", "https://passport.weibo.com/");
        form.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        using var response = await _http.PostAsync(GenVisitor2Url, form, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);

        var payload = UnwrapJsonp(raw);
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sub = ReadString(data, "sub");
        var subp = ReadString(data, "subp");
        return BuildCookieHeader(sub, subp);
    }

    // fallback：DDBOT 三步握手（genvisitor 取 tid → incarnate → crossdomain 取 sub/subp）
    private async Task<string?> TryFallbackAsync(CancellationToken ct)
    {
        var genRaw = await _http.GetStringAsync($"{GenVisitorUrl}?cb=gen_callback&from=weibo", ct);
        var genDoc = ParseUnwrapped(genRaw);
        var tid = TryGetStringPath(genDoc, ["data", "tid"]);

        if (string.IsNullOrEmpty(tid))
        {
            return null;
        }

        var incarnateRaw = await _http.GetStringAsync(
            $"{IncarnateUrl}?a=incarnate&t={Uri.EscapeDataString(tid)}&w=2&c=&gc=&cb=cross_domain", ct);
        _ = incarnateRaw; // 三步中的中间态，随 crossdomain 返回最终 sub/subp

        var crossRaw = await _http.GetStringAsync(
            $"{CrossDomainUrl}?a=crossdomain&cb=weibo_visitor_cross_domain&c=&s=&sp=", ct);
        var crossDoc = ParseUnwrapped(crossRaw);

        var sub = TryGetStringPath(crossDoc, ["data", "sub"])
            ?? TryGetStringPath(crossDoc, ["data", "tid"]);
        var subp = TryGetStringPath(crossDoc, ["data", "subp"]);
        return BuildCookieHeader(sub, subp);
    }

    private static string BuildCookieHeader(string? sub, string? subp)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(sub))
        {
            parts.Add($"SUB={Uri.EscapeDataString(sub)}");
        }

        if (!string.IsNullOrEmpty(subp))
        {
            parts.Add($"SUBP={Uri.EscapeDataString(subp)}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : string.Empty;
    }

    private static string UnwrapJsonp(string raw)
    {
        var trimmed = raw.Trim();
        var open = trimmed.IndexOf('(');
        var close = trimmed.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            return trimmed[(open + 1)..close];
        }

        return trimmed;
    }

    private static JsonDocument ParseUnwrapped(string raw)
    {
        try
        {
            return JsonDocument.Parse(UnwrapJsonp(raw));
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string? TryGetStringPath(JsonDocument doc, string[] path)
    {
        JsonElement current = doc.RootElement;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
