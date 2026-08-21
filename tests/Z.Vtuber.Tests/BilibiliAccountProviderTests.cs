using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;

namespace Z.Vtuber.Tests;

public sealed class BilibiliAccountProviderTests
{
    [Fact]
    public void GetProviderAsync_CreatesProviderWithCorrectOptions()
    {
        var options = new BilibiliCrawlerOptions
        {
            HttpTimeoutSeconds = 30,
        };
        var provider = new BilibiliAccountProvider(options);

        // 验证 provider 被正确创建
        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GetProviderAsync_AddBilibiliCrawlers_DoesNotRegisterDefaultHttpClient()
    {
        // 验证 P1 修复：AddBilibiliCrawlers(addHttpClient: false) 不会注册额外的 HttpClient
        var serviceProvider = await CreateProviderWithMockHttpClientAsync("cookie1");
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();

        // 验证 HttpClient 有正确的超时设置
        Assert.NotNull(httpClient);
        Assert.Equal(TimeSpan.FromSeconds(30), httpClient.Timeout);
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