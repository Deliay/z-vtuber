using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Tests;

public sealed class BrowserSessionRestoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"z-vtuber-restore-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private sealed class Harness
    {
        public int LaunchCount;
        public bool ThrowOnLaunch;
        public string PageUrl = "https://m.weibo.cn/";
        public string ApiBody = """{"data":{"login":true}}""";
        public Func<Task<string>>? Evaluate;
        public List<IPage> Pages = [];
        private IPage? _fallbackPage;

        public IPage Page => _fallbackPage ??= TestProxy.Create<IPage>(HandlePage);
        public IBrowserContext Context => TestProxy.Create<IBrowserContext>(HandleContext);
        public IBrowserType BrowserType => TestProxy.Create<IBrowserType>(HandleBrowserType);

        private object? HandleBrowserType(string name, object?[] args)
        {
            if (name == "LaunchPersistentContextAsync")
            {
                LaunchCount++;
                if (ThrowOnLaunch)
                {
                    throw new InvalidOperationException("launch failed");
                }
                return Task.FromResult(Context);
            }

            return null;
        }

        private object? HandleContext(string name, object?[] args)
        {
            return name switch
            {
                "get_Pages" => Pages.Count > 0 ? (object)Pages : new List<IPage>(),
                "get_APIRequest" => TestProxy.Create<IAPIRequestContext>(HandleApiRequest),
                "NewPageAsync" => Task.FromResult(Page),
                "CookiesAsync" => Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>(
                    new List<BrowserContextCookiesResult>()),
                "CloseAsync" => Task.CompletedTask,
                "DisposeAsync" => ValueTask.CompletedTask,
                _ => null,
            };
        }

        private object? HandleApiRequest(string name, object?[] args)
            => name == "GetAsync"
                ? Task.FromResult(TestProxy.Create<IAPIResponse>(HandleApiResponse))
                : null;

        private object? HandleApiResponse(string name, object?[] args)
            => name == "TextAsync" ? Task.FromResult(ApiBody) : null;

        private object? HandlePage(string name, object?[] args)
        {
            return name switch
            {
                "get_Url" => PageUrl,
                "GotoAsync" => Task.FromResult<IResponse?>(null),
                "EvaluateAsync" => (object)(Evaluate?.Invoke() ?? Task.FromResult("{\"status\":200,\"body\":\"{}\"}")),
                "WaitForSelectorAsync" => Task.FromResult<IElementHandle?>(null),
                "QuerySelectorAsync" => Task.FromResult<IElementHandle?>(null),
                "ScreenshotAsync" => Task.FromResult(new byte[] { 1, 2, 3 }),
                _ => null,
            };
        }
    }

    private BrowserSessionManager CreateManager(
        Harness harness,
        FakeCookieSessionRepository repository,
        CookieServiceOptions? options = null) =>
        new(new WeiboCookiePlatformProfile(),
            harness.BrowserType,
            repository,
            Options.Create(options ?? new CookieServiceOptions
            {
                BrowserSessionDataDir = _dataDir,
                ProxyRequestTimeoutSeconds = 5,
            }),
            NullLogger<BrowserSessionManager>.Instance);

    private BrowserSessionManager CreateBilibiliManager(Harness harness, FakeCookieSessionRepository repository) =>
        new(new BilibiliCookiePlatformProfile(),
            harness.BrowserType,
            repository,
            Options.Create(new CookieServiceOptions { BrowserSessionDataDir = _dataDir, RefreshNavTimeoutSeconds = 1 }),
            NullLogger<BrowserSessionManager>.Instance);

    private (FakeCookieSessionRepository Repo, ObjectId Id) CreateActiveSession(bool createDir)
    {
        var repo = new FakeCookieSessionRepository();
        var id = ObjectId.GenerateNewId();
        var userDataDir = Path.Combine(_dataDir, id.ToString());
        if (createDir)
        {
            Directory.CreateDirectory(userDataDir);
        }

        repo.Sessions.Add(new CookieSessionRecord
        {
            Id = id,
            Status = "active",
            UserDataDir = userDataDir,
            CookieId = id.ToString(),
        });
        return (repo, id);
    }

    [Fact]
    public async Task ExtractCookies_MissWithActiveSessionAndDir_RestoresOnce_ThenHitsCache()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo);

        var first = await manager.ExtractCookiesAsync(id.ToString());
        var second = await manager.ExtractCookiesAsync(id.ToString());

        Assert.NotNull(first);
        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task ExtractCookies_InactiveSession_DoesNotLaunch_Throws()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var (repo, id) = CreateActiveSession(createDir: true);
        repo.Sessions[0].Status = "invalid";
        var manager = CreateManager(harness, repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExtractCookiesAsync(id.ToString()));
        Assert.Equal(0, harness.LaunchCount);
    }

    [Fact]
    public async Task ExtractCookies_MissingUserDataDir_DoesNotLaunch_Throws()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var (repo, id) = CreateActiveSession(createDir: false);
        var manager = CreateManager(harness, repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExtractCookiesAsync(id.ToString()));
        Assert.Equal(0, harness.LaunchCount);
    }

    [Fact]
    public async Task ExtractCookies_LaunchFailure_Propagates()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness { ThrowOnLaunch = true };
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExtractCookiesAsync(id.ToString()));
        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task ProxyRequest_ReturnsStatusAndBody()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness
        {
            Evaluate = () => Task.FromResult("{\"status\":200,\"body\":\"payload\"}"),
        };
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo);

        var result = await manager.ProxyRequestAsync(id.ToString(), "/api/container/getIndex?containerid=1");

        Assert.Equal(200, result.Status);
        Assert.Equal("payload", result.Body);
        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task ProxyRequest_TimesOut_ThrowsTimeoutException()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness
        {
            Evaluate = () => new TaskCompletionSource<string>().Task,
        };
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo, new CookieServiceOptions
        {
            BrowserSessionDataDir = _dataDir,
            ProxyRequestTimeoutSeconds = 1,
        });

        await Assert.ThrowsAsync<TimeoutException>(() => manager.ProxyRequestAsync(id.ToString(), "/api/container/getIndex?containerid=1"));
    }

    [Fact]
    public async Task CreateLoginSession_ReturnsQrCodeImage()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var manager = CreateManager(harness, new FakeCookieSessionRepository());
        var accountId = ObjectId.GenerateNewId().ToString();

        var result = await manager.CreateLoginSessionAsync(accountId);

        Assert.Equal(accountId, result.AccountId);
        Assert.False(string.IsNullOrEmpty(result.QrCodeImage));
        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task DeleteSession_ClosesContext_NoThrow()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var manager = CreateManager(harness, new FakeCookieSessionRepository());
        var accountId = ObjectId.GenerateNewId().ToString();
        await manager.CreateLoginSessionAsync(accountId);

        await manager.DeleteSessionAsync(accountId);

        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task RefreshSession_WeiboProbeLoggedIn_Succeeds()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness { ApiBody = """{"data":{"login":true}}""" };
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo);

        await manager.RefreshSessionAsync(id.ToString());

        Assert.Equal(1, harness.LaunchCount);
    }

    [Fact]
    public async Task RefreshSession_WeiboProbeNotLoggedIn_Throws()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness { ApiBody = """{"data":{"login":false}}""" };
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RefreshSessionAsync(id.ToString()));
    }

    [Fact]
    public async Task WaitForNavResponse_NoContext_ReturnsNull()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var manager = CreateManager(harness, new FakeCookieSessionRepository());

        var result = await manager.WaitForNavResponseAsync(ObjectId.GenerateNewId().ToString(), TimeSpan.FromMilliseconds(50));

        Assert.Null(result);
    }

    [Fact]
    public async Task WaitForNavResponse_WeiboProbeLoggedIn_ReturnsBody()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness { ApiBody = """{"data":{"login":true}}""" };
        var (repo, id) = CreateActiveSession(createDir: true);
        var manager = CreateManager(harness, repo);
        await manager.ExtractCookiesAsync(id.ToString());

        var result = await manager.WaitForNavResponseAsync(id.ToString(), TimeSpan.FromSeconds(1));

        Assert.Equal(harness.ApiBody, result);
    }

    [Fact]
    public async Task WaitForNavResponse_BilibiliNav_TimeoutReturnsNull()
    {
        Directory.CreateDirectory(_dataDir);
        var harness = new Harness();
        var manager = CreateBilibiliManager(harness, new FakeCookieSessionRepository());
        var accountId = ObjectId.GenerateNewId().ToString();
        await manager.CreateLoginSessionAsync(accountId);

        var result = await manager.WaitForNavResponseAsync(accountId, TimeSpan.FromMilliseconds(50));

        Assert.Null(result);
    }
}
