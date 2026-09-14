using System.Net;
using System.Text.Json;

namespace Z.Vtuber.Crawler.Weibo;

public sealed class WeiboRiskControlException : Exception
{
    public WeiboRiskControlException(string message) : base(message) { }

    /// <summary>命中风控时使用的登录 cookieId；匿名凭证为 <c>null</c>。</summary>
    public string? CookieId { get; init; }

    /// <summary>命中风控时是否使用登录 cookie（false=匿名访客凭证）。</summary>
    public bool IsLoginCookie { get; init; }
}

public sealed class WeiboRiskControlClassifier
{
    private static readonly string[] LoginKeywords =
        ["登录", "cookie", "visitor", "auth", "请重新登录", "login"];

    public bool IsRiskControl(HttpStatusCode statusCode, string? contentType, string? body, string? location = null)
    {
        // 401 / 403：凭证被拒绝或越权
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        // 3xx 跳转至微博 passport / sina 登录页：凭证失效
        if (statusCode >= HttpStatusCode.MultipleChoices && statusCode < HttpStatusCode.BadRequest
            && !string.IsNullOrEmpty(location)
            && (location.Contains("passport.weibo.com", StringComparison.OrdinalIgnoreCase)
                || location.Contains("login.sina.com.cn", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        // 仅在 JSON 的 msg/error 字段上做登录/凭证失效关键词判断，避免误伤正常正文
        if (!TryParseJsonObject(body, out var obj))
        {
            // 预期 JSON 却返回 HTML/其它非 JSON：状态码非 2xx（可疑路径）判风控；
            // 2xx 的 HTML（如正常页面）不判，避免误伤。
            return IsSuspiciousStatus(statusCode) && IsNonJsonContentType(contentType);
        }

        // JSON 响应 ok != 1：业务失败（风控 / 无效凭证）
        if (TryGetOkFlag(obj, out var ok) && ok != 1)
        {
            return true;
        }

        // msg/error 字段含登录/凭证失效关键词
        foreach (var field in new[] { "msg", "error", "errmsg" })
        {
            if (obj.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
                && LoginKeywords.Any(k => el.GetString()?.Contains(k, StringComparison.OrdinalIgnoreCase) == true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSuspiciousStatus(HttpStatusCode statusCode) =>
        (int)statusCode < 200 || (int)statusCode >= 300;

    private static bool IsNonJsonContentType(string? contentType) =>
        string.IsNullOrEmpty(contentType)
        || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJsonObject(string body, out JsonElement obj)
    {
        obj = default;
        if (string.IsNullOrEmpty(body) || body[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                obj = doc.RootElement.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // malformed JSON —— 不误判
        }

        return false;
    }

    private static bool TryGetOkFlag(JsonElement obj, out int ok)
    {
        ok = default;
        if (!obj.TryGetProperty("ok", out var okEl)
            || (okEl.ValueKind != JsonValueKind.Number && okEl.ValueKind != JsonValueKind.String))
        {
            return false;
        }

        return int.TryParse(okEl.ToString(), out ok);
    }
}
