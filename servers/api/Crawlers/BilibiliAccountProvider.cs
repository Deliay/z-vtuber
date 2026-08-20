using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Configuration;

namespace Z.Vtuber.Crawlers;

public interface IBilibiliAccountProvider
{
    Task<IServiceProvider> GetProviderAsync(string cookie, CancellationToken ct = default);
}

public sealed class BilibiliAccountProvider(BilibiliCrawlerOptions options) : IBilibiliAccountProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IServiceProvider>>> _providers = new();

    public Task<IServiceProvider> GetProviderAsync(string cookie, CancellationToken ct = default)
    {
        var lazy = _providers.GetOrAdd(cookie, static (c, opts) => new Lazy<Task<IServiceProvider>>(
            () => BuildProviderAsync(c, opts)), options);
        return lazy.Value;
    }

    private static async Task<IServiceProvider> BuildProviderAsync(string cookie, BilibiliCrawlerOptions options)
    {
        var services = new ServiceCollection();
        // 挂入 RiskControlHandler：直连通道命中风控时抛 RiskControlException，供装饰器降级
        services.AddSingleton(new HttpClient(new RiskControlHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
        });
        services.AddSingleton(new CookieJar(cookie));
        services.AddBilibiliCrawlers();

        var provider = services.BuildServiceProvider();
        var account = provider.GetRequiredService<BilibiliAccount>();
        await account.InitializeAsync(CancellationToken.None);
        return provider;
    }
}
