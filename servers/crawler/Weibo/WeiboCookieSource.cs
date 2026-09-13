using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Weibo;

/// <summary>
/// 微博请求使用的 cookie 来源：主（登录 cookie）+ 兜底（匿名访客凭证）组合。
/// 登录路径返回的 <see cref="WeiboCookieLease"/> 持有该 cookie 的 per-cookie 锁，
/// 调用方必须在 HTTP 调用期间持有、结束后释放，保证同一 cookie 的请求串行。
/// </summary>
public interface IWeiboCookieSource
{
    Task<WeiboCookieLease> AcquireAsync(CancellationToken ct = default);
}

public sealed class WeiboCookieLease(string cookie, bool isLoginCookie, string? cookieId, IAsyncDisposable? inner = null)
    : IAsyncDisposable
{
    public string Cookie { get; } = cookie;
    public bool IsLoginCookie { get; } = isLoginCookie;
    public string? CookieId { get; } = cookieId;

    private readonly IAsyncDisposable? _inner = inner;

    public ValueTask DisposeAsync() => _inner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public sealed class WeiboCookieSource : IWeiboCookieSource
{
    private readonly WeiboCrawlerOptions _options;
    private readonly IWeiboLoginCookieProvider _loginCookies;
    private readonly IWeiboVisitorCookieProvider _visitorCookies;
    private readonly ILogger<WeiboCookieSource> _logger;

    public WeiboCookieSource(
        WeiboCrawlerOptions options,
        IWeiboLoginCookieProvider loginCookies,
        IWeiboVisitorCookieProvider visitorCookies,
        ILogger<WeiboCookieSource> logger)
    {
        _options = options;
        _loginCookies = loginCookies;
        _visitorCookies = visitorCookies;
        _logger = logger;
    }

    public async Task<WeiboCookieLease> AcquireAsync(CancellationToken ct = default)
    {
        if (_options.UseLoginCookie)
        {
            WeiboLoginCookieLease? login = null;
            try
            {
                login = await _loginCookies.AcquireAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Weibo login cookie checkout failed; falling back to visitor cookie.");
            }

            if (login is not null)
            {
                return new WeiboCookieLease(login.Cookie.Cookie, true, login.Cookie.CookieId, login);
            }

            _logger.LogWarning("No available weibo login cookie; falling back to visitor cookie.");
        }

        var visitor = await _visitorCookies.GetOrRefreshAsync(ct);
        return new WeiboCookieLease(visitor, false, null);
    }
}
