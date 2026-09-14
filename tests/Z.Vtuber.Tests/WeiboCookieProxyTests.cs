using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Tests;

public sealed class WeiboCookieProxyTests
{
    private const string CookieId = "64b000000000000000000001";

    private static WeiboCookieDispatchController CreateController(
        FakeCookiePoolService pool,
        FakeCookieSessionRepository repository,
        FakeBrowserSessionManager browser,
        FakeDistributedLockProvider? lockProvider = null,
        CookieServiceOptions? options = null) =>
        new(new FakeCookieDispatchService(),
            new FakeInvalidationService(),
            pool,
            repository,
            browser,
            new WeiboCookiePlatformProfile(),
            lockProvider ?? new FakeDistributedLockProvider(),
            Options.Create(options ?? new CookieServiceOptions()),
            NullLogger<WeiboCookieDispatchController>.Instance);

    private static (FakeCookiePoolService Pool, FakeCookieSessionRepository Repo, ObjectId SessionId) Seed(
        string cookieId = CookieId,
        string sessionStatus = "active",
        string poolStatus = CookieStatus.Active)
    {
        var sessionId = ObjectId.GenerateNewId();
        var pool = new FakeCookiePoolService
        {
            FindByCookieIdAsyncFunc = (id, _) => Task.FromResult<CookieRecord?>(id == cookieId
                ? new CookieRecord
                {
                    Id = ObjectId.Parse(cookieId),
                    AccountId = sessionId.ToString(),
                    Status = poolStatus,
                }
                : null),
        };

        var repo = new FakeCookieSessionRepository();
        repo.Sessions.Add(new CookieSessionRecord
        {
            Id = sessionId,
            Status = sessionStatus,
            CookieId = cookieId,
        });

        return (pool, repo, sessionId);
    }

    private static ProxyWeiboRequest Request(string path = "/api/container/getIndex?containerid=1076031", string cookieId = CookieId)
        => new() { CookieId = cookieId, Path = path };

    [Fact]
    public async Task Proxy_Whitelisted_ReturnsStatusAndBody()
    {
        var (pool, repo, sessionId) = Seed();
        var browser = new FakeBrowserSessionManager
        {
            ProxyRequestAsyncFunc = (_, _, _) => Task.FromResult(new ProxyFetchResult { Status = 200, Body = "payload" }),
        };
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<ProxyWeiboResponse>(ok.Value);
        Assert.Equal(200, response.Status);
        Assert.Equal("payload", response.Body);
        var call = Assert.Single(browser.ProxyRequests);
        Assert.Equal(sessionId.ToString(), call.AccountId);
        Assert.Equal("/api/container/getIndex?containerid=1076031", call.Path);
    }

    [Fact]
    public async Task Proxy_BusinessStatus_PassedThrough()
    {
        var (pool, repo, _) = Seed();
        var browser = new FakeBrowserSessionManager
        {
            ProxyRequestAsyncFunc = (_, _, _) => Task.FromResult(new ProxyFetchResult { Status = 403, Body = "forbidden" }),
        };
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<ProxyWeiboResponse>(ok.Value);
        Assert.Equal(403, response.Status);
        Assert.Equal("forbidden", response.Body);
    }

    [Theory]
    [InlineData("https://evil.com/api/x")]
    [InlineData("//evil.com/api/x")]
    [InlineData("/api/../../etc/passwd")]
    [InlineData("/status/x")]
    [InlineData("api/container/getIndex")]
    public async Task Proxy_NonWhitelisted_Returns400_NoBrowserCall(string path)
    {
        var (pool, repo, _) = Seed();
        var browser = new FakeBrowserSessionManager();
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(path), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(browser.ProxyRequests);
    }

    [Fact]
    public async Task Proxy_UnknownCookieId_Returns404()
    {
        var (pool, repo, _) = Seed();
        var browser = new FakeBrowserSessionManager();
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(cookieId: "64b0000000000000000000ff"), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(browser.ProxyRequests);
    }

    [Fact]
    public async Task Proxy_OfflineCookie_Returns404()
    {
        var (pool, repo, _) = Seed(poolStatus: CookieStatus.Offline);
        var browser = new FakeBrowserSessionManager();
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(browser.ProxyRequests);
    }

    [Fact]
    public async Task Proxy_InactiveSession_Returns410()
    {
        var (pool, repo, _) = Seed(sessionStatus: "invalid");
        var browser = new FakeBrowserSessionManager();
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, objectResult.StatusCode);
        Assert.Empty(browser.ProxyRequests);
    }

    [Fact]
    public async Task Proxy_MissingSession_Returns410()
    {
        var pool = new FakeCookiePoolService
        {
            FindByCookieIdAsyncFunc = (_, _) => Task.FromResult<CookieRecord?>(new CookieRecord
            {
                Id = ObjectId.Parse(CookieId),
                AccountId = "acc",
                Status = CookieStatus.Active,
            }),
        };
        var repo = new FakeCookieSessionRepository();
        var browser = new FakeBrowserSessionManager();
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, objectResult.StatusCode);
    }

    [Fact]
    public async Task Proxy_BrowserFailure_Returns502()
    {
        var (pool, repo, _) = Seed();
        var browser = new FakeBrowserSessionManager
        {
            ProxyRequestAsyncFunc = (_, _, _) => throw new BrowserProxyException("boom"),
        };
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
    }

    [Fact]
    public async Task Proxy_Timeout_Returns504()
    {
        var (pool, repo, _) = Seed();
        var browser = new FakeBrowserSessionManager
        {
            ProxyRequestAsyncFunc = (_, _, _) => throw new TimeoutException("timed out"),
        };
        var controller = CreateController(pool, repo, browser);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(504, objectResult.StatusCode);
    }

    [Fact]
    public async Task Proxy_LockUnavailable_Returns504_NoBrowserCall()
    {
        var (pool, repo, sessionId) = Seed();
        var browser = new FakeBrowserSessionManager();
        var lockProvider = new FakeDistributedLockProvider();
        var options = new CookieServiceOptions { LockAcquireWaitMs = 1 };

        var held = await lockProvider.AcquireAsync(
            $"lock:cookie-refresh:weibo:{sessionId}",
            TimeSpan.FromSeconds(90),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Assert.NotNull(held);

        var controller = CreateController(pool, repo, browser, lockProvider, options);

        var result = await controller.Proxy(Request(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(504, objectResult.StatusCode);
        Assert.Empty(browser.ProxyRequests);

        await held!.DisposeAsync();
    }

    [Fact]
    public async Task Proxy_MissingCookieIdOrPath_Returns400()
    {
        var (pool, repo, _) = Seed();
        var controller = CreateController(pool, repo, new FakeBrowserSessionManager());

        var result = await controller.Proxy(new ProxyWeiboRequest { CookieId = "", Path = "/api/x" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
