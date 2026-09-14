namespace Z.Vtuber.Cookie.Browser;

public interface IBrowserSessionManager
{
    Task<LoginSessionResult> CreateLoginSessionAsync(string accountId, CancellationToken ct = default);
    Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default);
    Task<string> ExtractCookiesAsync(string accountId, CancellationToken ct = default);
    Task DeleteSessionAsync(string accountId, CancellationToken ct = default);
    Task RefreshSessionAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// 在该帐号登录浏览器会话的页面上下文内发起同源 XHR，原样回传微博状态码与响应体。
    /// 浏览器侧网络/执行失败抛 <see cref="BrowserProxyException"/>，整体超时抛 <see cref="TimeoutException"/>。
    /// </summary>
    Task<ProxyFetchResult> ProxyRequestAsync(string accountId, string path, CancellationToken ct = default);
}

/// <summary>页面上下文 XHR 的原始结果。</summary>
public sealed class ProxyFetchResult
{
    public int Status { get; set; }
    public string Body { get; set; } = string.Empty;
}

/// <summary>浏览器侧请求失败（网络/页面执行异常），由控制器映射为 502。</summary>
public sealed class BrowserProxyException : Exception
{
    public BrowserProxyException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
