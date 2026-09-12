using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class BilibiliAccountProviderTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliCrawlerOptions Options => new() { HttpTimeoutSeconds = 30 };

    private static CookieCallGate CreateGate() => new(new FakeDistributedLockProvider());

    [Fact]
    public void GetProviderAsync_CreatesProviderWithCorrectOptions()
    {
        var provider = new BilibiliAccountProvider(Options, new FakeCookieServiceClient(), CreateGate(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BilibiliAccountProvider>.Instance);

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetProviderAsync_AddBilibiliCrawlers_DoesNotRegisterDefaultHttpClient()
    {
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

        public FailingProvider(BilibiliCrawlerOptions options, ICookieServiceClient client, Func<Exception> failureFactory, int failTimes)
            : base(options, client, CreateGate(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BilibiliAccountProvider>.Instance)
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
    public async Task GetProviderAsync_BuildFailure_ReportsInvalidAndRetriesWithNewCookie()
    {
        var client = new FakeCookieServiceClient
        {
            CheckoutFunc = _ => Task.FromResult(new CookieCheckoutResult
            {
                CookieId = "retry-id",
                Cookie = "retry-cookie",
            }),
        };
        var provider = new FailingProvider(
            Options, client, () => new InvalidOperationException("nav assert code"), failTimes: 1);

        var result = await provider.GetProviderAsync("cookie1", "cookie1");

        Assert.NotNull(result);
        Assert.Single(client.ReportInvalidCalls);
        Assert.Equal("account_provider_build_failed", client.ReportInvalidCalls[0].Reason);
        Assert.Equal(2, provider.BuildCount);
    }

    [Fact]
    public async Task GetProviderAsync_Success_KeepsCacheAndDoesNotReport()
    {
        var client = new FakeCookieServiceClient();
        var provider = new FailingProvider(
            Options, client, () => new InvalidOperationException("nav assert code"), failTimes: 0);

        var first = await provider.GetProviderAsync("cookie1", "cookie1");
        var second = await provider.GetProviderAsync("cookie1", "cookie1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, provider.BuildCount);
        Assert.Empty(client.ReportInvalidCalls);
    }

    [Fact]
    public async Task GetProviderAsync_RiskControl_ClearsCacheButDoesNotReport()
    {
        var client = new FakeCookieServiceClient();
        var provider = new FailingProvider(
            Options, client, () => new RiskControlException("risk", "http://test", "cookie1"), failTimes: 1);

        await Assert.ThrowsAsync<RiskControlException>(() => provider.GetProviderAsync("cookie1", "cookie1"));
        Assert.Empty(client.ReportInvalidCalls);

        var second = await provider.GetProviderAsync("cookie1", "cookie1");
        Assert.NotNull(second);
        Assert.Equal(2, provider.BuildCount);
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
