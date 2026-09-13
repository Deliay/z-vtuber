using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Crawlers;

public interface IBilibiliAccountProvider
{
    Task<IServiceProvider> GetProviderAsync(string cookieId, string cookie, CancellationToken ct = default);
}

/// <summary>
/// 按 Cookie 构建并缓存 BilibiliAccount 服务提供者。
/// 构建失败（如 nav/GetNavInfoV2 初始化失败，Mikibot AssertCode 抛无自定义消息的 InvalidOperationException）时：
/// 立即移除故障缓存（避免 faulted Lazy Task 永久重放），并将该 Cookie 标记离线，使后续 NextAsync 不再检出。
/// RiskControlException 不在此标记离线（由 resilient 客户端走既有 cooldown 路径），仅清理缓存后重抛。
/// 并发安全：cookie 粒度锁（CookieCallGate）确保同一 cookie 同一时刻仅一次构建与一次失效处置。
/// </summary>
public class BilibiliAccountProvider(
    BilibiliCrawlerOptions options,
    ICookieServiceClient cookieServiceClient,
    CookieCallGate gate,
    ILogger<BilibiliAccountProvider> logger) : IBilibiliAccountProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IServiceProvider>>> _providers = new();

    /// <summary>
    /// Public entry point: acquires cookie lock, then delegates to the lock-free internal core.
    /// </summary>
    public async Task<IServiceProvider> GetProviderAsync(string cookieId, string cookie, CancellationToken ct = default)
    {
        await using var _ = await gate.AcquireAsync(cookieId, ct);
        return await GetProviderInternalAsync(cookieId, cookie, isRetry: false, ct);
    }

    /// <summary>
    /// Lock-free internal core. Caller MUST hold the cookie lock via <see cref="CookieCallGate"/>.
    /// Used by both <see cref="GetProviderAsync"/> and <see cref="Live.DanmakuClientFactory"/>.
    /// </summary>
    internal async Task<IServiceProvider> GetProviderInternalAsync(string cookieId, string cookie, bool isRetry = false, CancellationToken ct = default)
    {
        // Fast path: if a cached entry exists and has completed, return immediately.
        if (_providers.TryGetValue(cookie, out var existing) && existing.IsValueCreated)
        {
            return await existing.Value;
        }

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

            // Only report invalid on first attempt; retry path should not re-enter report-invalid
            if (isRetry)
            {
                throw;
            }

            // Report invalid to cookie service and use the response to decide next action
            try
            {
                var reportResult = await cookieServiceClient.ReportInvalidAsync(cookieId, ct);
                if (reportResult.Status == ReportInvalidStatus.Refreshed && !string.IsNullOrEmpty(reportResult.Cookie))
                {
                    // Use new cookie from response to rebuild provider (one retry only, no report-invalid)
                    return await GetProviderInternalAsync(reportResult.CookieId!, reportResult.Cookie!, isRetry: true, ct);
                }

                // Session invalid, not found, timeout, or Refreshed with null cookie — give up
                throw new CookieSessionInvalidException();
            }
            catch (NoAvailableCookieException)
            {
                logger.LogWarning("No available cookie for retry after build failure.");
                throw;
            }
            catch (CookieSessionInvalidException)
            {
                throw;
            }
            catch (Exception reportEx)
            {
                logger.LogWarning(reportEx, "Failed to report invalid cookie or retry build; continuing.");
                throw;
            }
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

public sealed class CookieSessionInvalidException : Exception
{
    public CookieSessionInvalidException()
        : base("Cookie session is no longer valid.") { }
}
