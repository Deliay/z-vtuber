using System.Net;
using System.Text.Json;

namespace Z.Vtuber.Crawler.Weibo;

public sealed class WeiboRiskControlException : Exception
{
    public WeiboRiskControlException(string message) : base(message) { }
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

        // 3xx 跳转至微博 passport 登录页：访客凭证失效
        if (statusCode >= HttpStatusCode.MultipleChoices && statusCode < HttpStatusCode.BadRequest
            && !string.IsNullOrEmpty(location)
            && location.Contains("passport.weibo.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        // JSON 响应 ok != 1：业务失败（风控 / 无效凭证）
        if (TryGetOkFlag(body, out var ok) && ok != 1)
        {
            return true;
        }

        // 响应文本含登录/凭证失效关键词
        if (LoginKeywords.Any(k => body.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetOkFlag(string body, out int ok)
    {
        ok = default;
        if (string.IsNullOrEmpty(body) || !(body[0] == '{' || body[0] == '['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ok", out var okEl)
                && (okEl.ValueKind == JsonValueKind.Number || okEl.ValueKind == JsonValueKind.String))
            {
                return int.TryParse(okEl.ToString(), out ok);
            }
        }
        catch (JsonException)
        {
            // malformed JSON —— 不误判
        }

        return false;
    }
}
