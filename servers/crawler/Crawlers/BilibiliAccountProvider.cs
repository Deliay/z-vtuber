using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Crawlers;

public interface IBilibiliAccountProvider
{
    Task<IServiceProvider> GetProviderAsync(string cookie, CancellationToken ct = default);
}

/// <summary>
/// 按 Cookie 构建并缓存 BilibiliAccount 服务提供者。
/// 构建失败（如 nav/GetNavInfoV2 初始化失败，Mikibot AssertCode 抛无自定义消息的 InvalidOperationException）时：
/// 立即移除故障缓存（避免 faulted Lazy Task 永久重放），并将该 Cookie 标记离线，使后续 NextAsync 不再检出。
/// RiskControlException 不在此标记离线（由 resilient 客户端走既有 cooldown 路径），仅清理缓存后重抛。
/// </summary>
public class BilibiliAccountProvider(
    BilibiliCrawlerOptions options,
    ICookiePoolService cookiePool) : IBilibiliAccountProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IServiceProvider>>> _providers = new();

    public async Task<IServiceProvider> GetProviderAsync(string cookie, CancellationToken ct = default)
    {
        var lazy = _providers.GetOrAdd(cookie, key => new Lazy<Task<IServiceProvider>>(
            () => BuildProviderAsync(key, options)));

        try
        {
            return await lazy.Value;
        }
        catch (RiskControlException)
        {
            _providers.TryRemove(new KeyValuePair<string, Lazy<Task<IServiceProvider>>>(cookie, lazy));
            throw;
        }
        catch (OperationCanceledException)
        {
            _providers.TryRemove(new KeyValuePair<string, Lazy<Task<IServiceProvider>>>(cookie, lazy));
            throw;
        }
        catch (Exception)
        {
            _providers.TryRemove(new KeyValuePair<string, Lazy<Task<IServiceProvider>>>(cookie, lazy));
            await cookiePool.MarkOfflineAsync(cookie, ct);
            throw;
        }
    }

    internal virtual Task<IServiceProvider> BuildProviderAsync(string cookie, BilibiliCrawlerOptions options) =>
        BuildProviderCoreAsync(cookie, options);

    private static async Task<IServiceProvider> BuildProviderCoreAsync(string cookie, BilibiliCrawlerOptions options)
    {
        var services = new ServiceCollection();
        // 挂入 RiskControlHandler：直连通道命中风控时抛 RiskControlException，供装饰器降级
        services.AddSingleton(new HttpClient(new RiskControlHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
        });
        services.AddSingleton(new CookieJar(cookie));
        services.AddBilibiliCrawlers(addHttpClient: false);

        var provider = services.BuildServiceProvider();
        var account = provider.GetRequiredService<BilibiliAccount>();
        await account.InitializeAsync(CancellationToken.None);
        return provider;
    }
}
