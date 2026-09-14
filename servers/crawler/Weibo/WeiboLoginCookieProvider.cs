using Z.Vtuber.Common.Locking;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Weibo;

/// <summary>
/// 微博登录 cookie 主凭证提供方：经 cookie 服务 checkout 取用登录态 cookie。
/// 与 B 站不同，微博有匿名访客兜底，因此「无可用」以 <c>null</c> 信号透出而非抛异常。
/// 同一 cookie 的请求经 <see cref="CookieCallGate"/> per-cookie 互斥（key <c>cookie-call:{cookieId}</c>）。
/// </summary>
public interface IWeiboLoginCookieProvider
{
    /// <summary>领取一个可用登录 cookie；无可用时返回 <c>null</c>（调用方回退匿名）。</summary>
    Task<WeiboLoginCookie?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// 领取登录 cookie 并持有其 per-cookie 锁。<see cref="WeiboLoginCookieLease"/> 被释放前，
    /// 同一 cookieId 的其它调用方将排队等待；无可用 cookie 时返回 <c>null</c>（不获取锁）。
    /// </summary>
    Task<WeiboLoginCookieLease?> AcquireAsync(CancellationToken ct = default);

    /// <summary>宣告登录 cookie 失效（阻塞式自愈）；结果语义同 B 站 report-invalid。</summary>
    Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default);
}

public sealed record WeiboLoginCookie(string CookieId, string Cookie, string? AccountId);

public sealed class WeiboLoginCookieLease(WeiboLoginCookie cookie, IDistributedLockHandle gate) : IAsyncDisposable
{
    public WeiboLoginCookie Cookie { get; } = cookie;

    private readonly IDistributedLockHandle _gate = gate;

    public ValueTask DisposeAsync() => _gate.DisposeAsync();
}

public sealed class WeiboLoginCookieProvider : IWeiboLoginCookieProvider
{
    private readonly ICookieServiceClient _client;
    private readonly CookieCallGate _gate;

    public WeiboLoginCookieProvider(ICookieServiceClient client, CookieCallGate gate)
    {
        _client = client;
        _gate = gate;
    }

    public async Task<WeiboLoginCookie?> GetAsync(CancellationToken ct = default)
    {
        var result = await _client.CheckoutWeiboAsync(ct);
        if (!result.Available
            || string.IsNullOrEmpty(result.CookieId)
            || string.IsNullOrEmpty(result.Cookie))
        {
            return null;
        }

        return new WeiboLoginCookie(result.CookieId, result.Cookie, result.AccountId);
    }

    public async Task<WeiboLoginCookieLease?> AcquireAsync(CancellationToken ct = default)
    {
        var cookie = await GetAsync(ct);
        if (cookie is null)
        {
            return null;
        }

        var gate = await _gate.AcquireAsync(cookie.CookieId, ct);
        return new WeiboLoginCookieLease(cookie, gate);
    }

    public Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default) =>
        _client.ReportWeiboInvalidAsync(cookieId, ct);
}
