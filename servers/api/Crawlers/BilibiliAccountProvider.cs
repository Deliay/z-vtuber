using System.Collections.Concurrent;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Configuration;

namespace Z.Vtuber.Crawlers;

public interface IBilibiliAccountProvider
{
    Task<BilibiliAccount> GetAsync(string cookie, CancellationToken ct = default);
}

public sealed class BilibiliAccountProvider(BilibiliCrawlerOptions options) : IBilibiliAccountProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BilibiliAccount>>> _accounts = new();

    public Task<BilibiliAccount> GetAsync(string cookie, CancellationToken ct = default)
    {
        var lazy = _accounts.GetOrAdd(cookie, static (c, opts) => new Lazy<Task<BilibiliAccount>>(
            () => InitAccountAsync(c, opts)), options);
        return lazy.Value;
    }

    private static async Task<BilibiliAccount> InitAccountAsync(string cookie, BilibiliCrawlerOptions options)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds) };
        var jar = new CookieJar(cookie);
        var basic = new BiliBasicInfoCrawler(http, jar);
        var account = new BilibiliAccount(basic);
        await account.InitializeAsync(CancellationToken.None);
        return account;
    }
}
