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

        // 仅在 JSON 的 msg/error 字段上做登录/凭证失效关键词判断，避免误伤正常正文
        if (!TryParseJsonObject(body, out var obj))
        {
            return false;
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
