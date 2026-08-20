using System.Net;
using System.Text.Json;

namespace Z.Vtuber.Crawlers;

public enum RiskSignal
{
    Http412,
    JsonCodeMinus412,
    HtmlRiskControlPage,
}

public sealed class RiskControlResult
{
    public bool IsMatch { get; }
    public RiskSignal Signal { get; }
    public TimeSpan? RetryAfter { get; }
    public string? CookieTag { get; }

    private RiskControlResult(bool isMatch, RiskSignal signal, TimeSpan? retryAfter, string? cookieTag)
    {
        IsMatch = isMatch;
        Signal = signal;
        RetryAfter = retryAfter;
        CookieTag = cookieTag;
    }

    public static RiskControlResult NoMatch => new(false, default, null, null);

    public static RiskControlResult Match(RiskSignal signal, TimeSpan? retryAfter = null, string? cookieTag = null)
        => new(true, signal, retryAfter, cookieTag);
}

public static class RiskControlClassifier
{
    private const string SafetyKeyword = "安全风控";
    private const string Error412Keyword = "错误号: 412";

    public static RiskControlResult Classify(
        HttpStatusCode statusCode,
        string? contentType,
        string? body)
    {
        if (statusCode == HttpStatusCode.PreconditionFailed)
        {
            return RiskControlResult.Match(RiskSignal.Http412);
        }

        if (string.IsNullOrEmpty(body))
        {
            return RiskControlResult.NoMatch;
        }

        if (IsHtmlContentType(contentType))
        {
            if (body.Contains(SafetyKeyword, StringComparison.Ordinal) || body.Contains(Error412Keyword, StringComparison.Ordinal))
            {
                return RiskControlResult.Match(RiskSignal.HtmlRiskControlPage);
            }
            return RiskControlResult.NoMatch;
        }

        if (IsJsonContentType(contentType))
        {
            return ClassifyJson(body);
        }

        return RiskControlResult.NoMatch;
    }

    private static bool IsHtmlContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        return contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        return contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/json", StringComparison.OrdinalIgnoreCase);
    }

    private static RiskControlResult ClassifyJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.Number
                && codeElement.GetInt32() == -412)
            {
                return RiskControlResult.Match(RiskSignal.JsonCodeMinus412);
            }
        }
        catch (JsonException)
        {
            // malformed JSON — do not misclassify
        }

        return RiskControlResult.NoMatch;
    }
}
