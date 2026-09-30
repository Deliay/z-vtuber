using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Crawler.Weibo;

namespace Z.Vtuber.Tests;

public sealed class WeiboLoginCookieProviderTests
{
    private static WeiboLoginCookieProvider CreateProvider(
        FakeCookieServiceClient client,
        FakeDistributedLockProvider? locks = null) =>
        new(client, new CookieCallGate(locks ?? new FakeDistributedLockProvider()));

    [Fact]
    public async Task GetAsync_CheckoutSuccess_ReturnsCookieAndId()
    {
        var client = new FakeCookieServiceClient
        {
            CheckoutWeiboFunc = _ => Task.FromResult(new WeiboCookieCheckoutResult
            {
                Available = true,
                CookieId = "c1",
                Cookie = "SUB=x",
                AccountId = "a1",
            }),
        };
        var provider = CreateProvider(client);

        var cookie = await provider.GetAsync();

        Assert.NotNull(cookie);
        Assert.Equal("c1", cookie!.CookieId);
        Assert.Equal("SUB=x", cookie.Cookie);
        Assert.Equal("a1", cookie.AccountId);
    }

    [Fact]
    public async Task GetAsync_NoAvailable_ReturnsNull_NoThrow()
    {
        var client = new FakeCookieServiceClient
        {
            CheckoutWeiboFunc = _ => Task.FromResult(new WeiboCookieCheckoutResult { Available = false }),
        };
        var provider = CreateProvider(client);

        var cookie = await provider.GetAsync();

        Assert.Null(cookie);
    }

    [Fact]
    public async Task GetAsync_AvailableButEmptyCookie_ReturnsNull()
    {
        var client = new FakeCookieServiceClient
        {
            CheckoutWeiboFunc = _ => Task.FromResult(new WeiboCookieCheckoutResult
            {
                Available = true,
                CookieId = "c1",
                Cookie = null,
            }),
        };
        var provider = CreateProvider(client);

        Assert.Null(await provider.GetAsync());
    }

    [Fact]
    public async Task AcquireAsync_NoAvailable_ReturnsNull_WithoutLocking()
    {
        var client = new FakeCookieServiceClient
        {
            CheckoutWeiboFunc = _ => Task.FromResult(new WeiboCookieCheckoutResult { Available = false }),
        };
        var provider = CreateProvider(client);

        Assert.Null(await provider.AcquireAsync());
    }

    [Fact]
    public async Task AcquireAsync_SameCookie_SerializesAcrossCallers()
    {
        var client = new FakeCookieServiceClient
        {
            CheckoutWeiboFunc = _ => Task.FromResult(new WeiboCookieCheckoutResult
            {
                Available = true,
                CookieId = "c1",
                Cookie = "SUB=x",
            }),
        };
        var lockProvider = new FakeDistributedLockProvider();
        var provider = CreateProvider(client, lockProvider);

        var first = await provider.AcquireAsync();
        Assert.NotNull(first);

        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lockProvider.LockWaitStarted = _ => waitStarted.TrySetResult();

        var secondTask = provider.AcquireAsync();
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondTask.IsCompleted);

        await first!.DisposeAsync();
        var second = await secondTask;
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_DifferentCookies_DoNotBlockEachOther()
    {
        var next = 0;
        var client = new FakeCookieServiceClient
        {
            CheckoutWeiboFunc = _ =>
            {
                var id = Interlocked.Increment(ref next) == 1 ? "c1" : "c2";
                return Task.FromResult(new WeiboCookieCheckoutResult
                {
                    Available = true,
                    CookieId = id,
                    Cookie = $"SUB={id}",
                });
            },
        };
        var provider = CreateProvider(client);

        var first = await provider.AcquireAsync();
        Assert.NotNull(first);
        Assert.Equal("c1", first!.Cookie.CookieId);

        var second = await provider.AcquireAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(second);
        Assert.Equal("c2", second!.Cookie.CookieId);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ReportInvalidAsync_DelegatesToWeiboEndpoint()
    {
        var client = new FakeCookieServiceClient();
        var provider = CreateProvider(client);

        var result = await provider.ReportInvalidAsync("c1");

        Assert.Equal(["c1"], client.ReportWeiboInvalidCalls);
        Assert.Equal(ReportInvalidStatus.Refreshed, result.Status);
    }
}
