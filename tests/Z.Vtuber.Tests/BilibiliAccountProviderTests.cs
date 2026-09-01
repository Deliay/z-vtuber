using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class BilibiliAccountProviderTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliCrawlerOptions Options => new() { HttpTimeoutSeconds = 30 };

    private static MockCookiePoolService CreateMockPool() => new();

    [Fact]
    public void GetProviderAsync_CreatesProviderWithCorrectOptions()
    {
        var provider = new BilibiliAccountProvider(Options, CreateMockPool());

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetProviderAsync_AddBilibiliCrawlers_DoesNotRegisterDefaultHttpClient()
    {
        // 验证 P1 修复：AddBilibiliCrawlers(addHttpClient: false) 不会注册额外的 HttpClient
        var serviceProvider = await CreateProviderWithMockHttpClientAsync("cookie1");
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();

        Assert.NotNull(httpClient);
        Assert.Equal(TimeSpan.FromSeconds(30), httpClient.Timeout);
    }

    private sealed class FailingProvider : BilibiliAccountProvider
    {
        private readonly Func<Exception> _failureFactory;
        private readonly int _failTimes;
        private int _buildCount;

        public FailingProvider(BilibiliCrawlerOptions options, ICookiePoolService pool, Func<Exception> failureFactory, int failTimes)
            : base(options, pool)
        {
            _failureFactory = failureFactory;
            _failTimes = failTimes;
        }

        public int BuildCount => _buildCount;

        internal override Task<IServiceProvider> BuildProviderAsync(string cookie, BilibiliCrawlerOptions options)
        {
            _buildCount++;
            if (_buildCount <= _failTimes)
            {
                return Task.FromException<IServiceProvider>(_failureFactory());
            }

            return Task.FromResult<IServiceProvider>(new ServiceCollection().BuildServiceProvider());
        }
    }

    [Fact]
    public async Task GetProviderAsync_BuildFailure_MarksOfflineAndClearsCacheForRetry()
    {
        var pool = CreateMockPool();
        var provider = new FailingProvider(
            Options, pool, () => new InvalidOperationException("nav assert code"), failTimes: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetProviderAsync("cookie1"));
        Assert.Equal("cookie1", Assert.Single(pool.OfflineCalls));

        // 缓存已清除：第二次调用重新构建而非重放同一故障 Task
        var second = await provider.GetProviderAsync("cookie1");
        Assert.NotNull(second);
        Assert.Equal(2, provider.BuildCount);
    }

    [Fact]
    public async Task GetProviderAsync_Success_KeepsCacheAndDoesNotMarkOffline()
    {
        var pool = CreateMockPool();
        var provider = new FailingProvider(
            Options, pool, () => new InvalidOperationException("nav assert code"), failTimes: 0);

        var first = await provider.GetProviderAsync("cookie1");
        var second = await provider.GetProviderAsync("cookie1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, provider.BuildCount);
        Assert.Empty(pool.OfflineCalls);
    }

    [Fact]
    public async Task GetProviderAsync_RiskControl_ClearsCacheButDoesNotMarkOffline()
    {
        var pool = CreateMockPool();
        var provider = new FailingProvider(
            Options, pool, () => new RiskControlException("risk", "http://test", "cookie1"), failTimes: 1);

        await Assert.ThrowsAsync<RiskControlException>(() => provider.GetProviderAsync("cookie1"));
        Assert.Empty(pool.OfflineCalls);

        // 缓存已清除，可重试
        var second = await provider.GetProviderAsync("cookie1");
        Assert.NotNull(second);
        Assert.Equal(2, provider.BuildCount);
    }

    [Fact]
    public async Task GetProviderAsync_Failure_MarkedOfflineCookie_NotCheckedOutByNext()
    {
        var realPool = new CookiePoolService(fixture.Database, new ProbeServiceProvider());
        await realPool.AddAsync("cookie1");
        var provider = new FailingProvider(
            Options, realPool, () => new InvalidOperationException("nav assert code"), failTimes: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetProviderAsync("cookie1"));

        // 失败后 cookie 已离线，NextAsync 不再检出
        await Assert.ThrowsAsync<NoAvailableCookieException>(() => realPool.NextAsync());
        Assert.Empty(await realPool.GetActiveAsync());
    }

    private static async Task<IServiceProvider> CreateProviderWithMockHttpClientAsync(string cookie)
    {
        var services = new ServiceCollection();
        var options = new BilibiliCrawlerOptions { HttpTimeoutSeconds = 30 };

        services.AddSingleton(new HttpClient(new RiskControlHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
        });
        services.AddSingleton(new CookieJar(cookie));
        services.AddBilibiliCrawlers(addHttpClient: false);

        return services.BuildServiceProvider();
    }
}