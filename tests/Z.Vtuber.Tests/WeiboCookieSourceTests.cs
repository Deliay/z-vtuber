using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Weibo;

namespace Z.Vtuber.Tests;

public sealed class WeiboCookieSourceTests
{
    private static WeiboCookieSource CreateSource(
        IWeiboLoginCookieProvider login,
        IWeiboVisitorCookieProvider visitor,
        WeiboCrawlerOptions? options = null) =>
        new(options ?? new WeiboCrawlerOptions(),
            login,
            visitor,
            NullLogger<WeiboCookieSource>.Instance);

    [Fact]
    public async Task UseLoginCookieFalse_UsesVisitor_NeverChecksOut()
    {
        var login = new FakeWeiboLoginCookieProvider { Cookie = new WeiboLoginCookie("c1", "SUB=login", null) };
        var visitor = new FakeWeiboVisitorCookieProvider { CookieHeader = "SUB=visitor" };
        var source = CreateSource(login, visitor, new WeiboCrawlerOptions { UseLoginCookie = false });

        await using var lease = await source.AcquireAsync();

        Assert.Equal(0, login.GetCalls);
        Assert.False(lease.IsLoginCookie);
        Assert.Null(lease.CookieId);
        Assert.Equal("SUB=visitor", lease.Cookie);
    }

    [Fact]
    public async Task UseLoginCookieTrue_NoAvailableLogin_FallsBackToVisitor()
    {
        var login = new FakeWeiboLoginCookieProvider { Cookie = null };
        var visitor = new FakeWeiboVisitorCookieProvider { CookieHeader = "SUB=visitor" };
        var source = CreateSource(login, visitor);

        await using var lease = await source.AcquireAsync();

        Assert.Equal(1, login.GetCalls);
        Assert.False(lease.IsLoginCookie);
        Assert.Null(lease.CookieId);
        Assert.Equal("SUB=visitor", lease.Cookie);
    }

    [Fact]
    public async Task UseLoginCookieTrue_WithLogin_UsesLoginCookie()
    {
        var login = new FakeWeiboLoginCookieProvider { Cookie = new WeiboLoginCookie("c1", "SUB=login", "a1") };
        var visitor = new FakeWeiboVisitorCookieProvider { CookieHeader = "SUB=visitor" };
        var source = CreateSource(login, visitor);

        await using var lease = await source.AcquireAsync();

        Assert.True(lease.IsLoginCookie);
        Assert.Equal("c1", lease.CookieId);
        Assert.Equal("SUB=login", lease.Cookie);
    }
}
