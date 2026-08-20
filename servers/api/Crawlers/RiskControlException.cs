using System.Net.Http;

namespace Z.Vtuber.Crawlers;

public sealed class RiskControlException : HttpRequestException
{
    public string Target { get; }
    public string? CookieTag { get; }
    public TimeSpan? RetryAfter { get; }

    public RiskControlException(string message, string target, string? cookieTag = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        Target = target;
        CookieTag = cookieTag;
        RetryAfter = retryAfter;
    }
}
