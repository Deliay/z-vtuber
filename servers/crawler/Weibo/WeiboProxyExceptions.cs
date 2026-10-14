namespace Z.Vtuber.Crawler.Weibo;

/// <summary>
/// 代理请求返回 410/404（关联会话失效或 cookie 不存在）——按会话失效处置：放弃本轮 + 退避，
/// cookie-invalid 事件由 cookie 服务发布，不再二次宣告。
/// </summary>
public sealed class WeiboProxySessionInvalidException : Exception
{
    public WeiboProxySessionInvalidException(string message) : base(message)
    {
    }

    public string? CookieId { get; init; }
}

/// <summary>
/// 代理自身失败（浏览器侧失败 502 / 超时 504）——记退避但<b>不</b>宣告 cookie 失效。
/// </summary>
public sealed class WeiboProxyUnavailableException : Exception
{
    public WeiboProxyUnavailableException(string message, bool timedOut = false) : base(message)
    {
        TimedOut = timedOut;
    }

    public bool TimedOut { get; }

    public string? CookieId { get; init; }
}
