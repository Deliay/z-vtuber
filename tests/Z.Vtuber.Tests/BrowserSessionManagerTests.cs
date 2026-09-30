using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Tests;

public sealed class BrowserSessionManagerTests
{
    private static (BrowserSessionManager Manager, FakeBrowserHarness Harness) Create(
        ICookiePlatformProfile profile,
        int idleSeconds,
        Func<string, FakeBrowserContext>? contextFactory = null)
    {
        var harness = new FakeBrowserHarness();
        if (contextFactory is not null)
        {
            harness.ContextFactory = contextFactory;
        }

        var options = Options.Create(new CookieServiceOptions
        {
            BrowserSessionDataDir = Path.Combine(Path.GetTempPath(), "zvt-cookie-" + Guid.NewGuid().ToString("N")),
            PageIdleCloseDelaySeconds = idleSeconds,
        });

        var manager = new BrowserSessionManager(
            profile,
            harness.BrowserType,
            options,
            NullLogger<BrowserSessionManager>.Instance);

        return (manager, harness);
    }

    [Fact]
    public async Task ExtractCookies_WhenContextMissing_LaunchesOnceAndReturnsCookies()
    {
        var (manager, harness) = Create(
            new BilibiliCookiePlatformProfile(),
            idleSeconds: 60,
            contextFactory: accountId =>
            {
                var context = new FakeBrowserContext(accountId);
                context.CookieResults.Add(new BrowserContextCookiesResult { Name = "SESSDATA", Value = "abc", Domain = ".bilibili.com" });
                context.CookieResults.Add(new BrowserContextCookiesResult { Name = "bili_jct", Value = "def", Domain = ".bilibili.com" });
                return context;
            });

        var cookie = await manager.ExtractCookiesAsync("acc-1");

        Assert.Equal("SESSDATA=abc; bili_jct=def", cookie);
        Assert.Equal(1, harness.LaunchCount);
        Assert.Single(harness.LaunchedUserDataDirs);
        Assert.EndsWith("acc-1", harness.LaunchedUserDataDirs[0]);
    }

    [Fact]
    public async Task ExtractCookies_WhenContextAlive_ReusesContext()
    {
        var (manager, harness) = Create(new BilibiliCookiePlatformProfile(), idleSeconds: 60);

        await manager.ExtractCookiesAsync("acc-1");
        await manager.ExtractCookiesAsync("acc-1");

        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task ExtractCookies_AfterIdleDelay_ClosesContext()
    {
        var (manager, harness) = Create(new BilibiliCookiePlatformProfile(), idleSeconds: 0);
        await using (manager)
        {
            await manager.ExtractCookiesAsync("acc-1");
            var context = harness.Contexts["acc-1"];

            await TestWait.UntilAsync(() => context.IsClosed, TimeSpan.FromSeconds(3));

            Assert.Equal(1, context.CloseCount);
        }
    }

    [Fact]
    public async Task ExtractCookies_WhenUsedAgainDuringIdle_DefersClose()
    {
        var (manager, harness) = Create(new BilibiliCookiePlatformProfile(), idleSeconds: 1);
        await using (manager)
        {
            await manager.ExtractCookiesAsync("acc-1");
            var context = harness.Contexts["acc-1"];

            await Task.Delay(400);
            await manager.ExtractCookiesAsync("acc-1");

            // 原始 1s 到期点已过，但复用已顺延空闲计时：不得关闭。
            await TestWait.NeverAsync(
                () => context.IsClosed,
                TimeSpan.FromMilliseconds(700),
                TimeSpan.FromMilliseconds(25),
                "idle close must be deferred after reuse");
            Assert.False(context.IsClosed);

            await TestWait.UntilAsync(() => context.IsClosed, TimeSpan.FromSeconds(5));
            Assert.Equal(1, context.CloseCount);
        }
    }

    [Fact]
    public async Task DeleteSession_ClosesImmediately_AndDoesNotCloseTwice()
    {
        var (manager, harness) = Create(new BilibiliCookiePlatformProfile(), idleSeconds: 1);
        await using (manager)
        {
            await manager.ExtractCookiesAsync("acc-1");
            var context = harness.Contexts["acc-1"];

            await manager.DeleteSessionAsync("acc-1");

            Assert.True(context.IsClosed);
            Assert.Equal(1, context.CloseCount);

            // 覆盖原 1s 空闲窗口，确认删除后空闲定时器已被取消、不会二次关闭。
            await TestWait.NeverAsync(
                () => context.CloseCount > 1,
                TimeSpan.FromMilliseconds(1100),
                TimeSpan.FromMilliseconds(25),
                "idle timer must be cancelled after delete");
            Assert.Equal(1, context.CloseCount);
        }
    }

    [Fact]
    public async Task Refresh_WeiboProbe_LaunchesContextAndUsesApiRequestWithoutPage()
    {
        var (manager, harness) = Create(
            new WeiboCookiePlatformProfile(),
            idleSeconds: 60,
            contextFactory: accountId =>
            {
                var context = new FakeBrowserContext(accountId);
                context.ApiRequest.ResponseBodyProvider = _ => """{"data":{"login":true,"uid":1}}""";
                return context;
            });

        await manager.RefreshSessionAsync("acc-weibo");

        Assert.Equal(1, harness.LaunchCount);
        var context = harness.Contexts["acc-weibo"];
        Assert.Equal(1, context.ApiRequest.GetCount);
        Assert.Equal(0, context.NewPageCount);
    }

    [Fact]
    public async Task Refresh_Bilibili_WhenNoPages_CreatesPageBeforeNavigation()
    {
        var (manager, harness) = Create(
            new BilibiliCookiePlatformProfile(),
            idleSeconds: 60,
            contextFactory: accountId =>
            {
                var context = new FakeBrowserContext(accountId);
                // 重开的 persistent context 首页集合为空，需按需新建页面。
                context.Pages.Clear();
                return context;
            });

        await manager.RefreshSessionAsync("acc-bili");

        var context = harness.Contexts["acc-bili"];
        Assert.Equal(1, context.NewPageCount);
        Assert.Single(context.CreatedPages);
    }

    [Fact]
    public async Task Refresh_Bilibili_WhenProbeFails_StillSchedulesIdleClose()
    {
        var (manager, harness) = Create(
            new BilibiliCookiePlatformProfile(),
            idleSeconds: 0,
            contextFactory: accountId =>
            {
                var context = new FakeBrowserContext(accountId);
                context.PageResponseBody = """{"code":-101,"data":{"isLogin":false}}""";
                return context;
            });

        await using (manager)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RefreshSessionAsync("acc-bili"));

            var context = harness.Contexts["acc-bili"];
            await TestWait.UntilAsync(() => context.IsClosed, TimeSpan.FromSeconds(3));
            Assert.Equal(1, context.CloseCount);
        }
    }
}
