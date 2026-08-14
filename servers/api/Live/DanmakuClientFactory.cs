using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.WebsocketCrawler.Client;
using Z.Vtuber.Configuration;

namespace Z.Vtuber.Live;

public sealed class DanmakuClientFactory(BilibiliCrawlerOptions options)
{
    public async Task<DanmakuClient> CreateAsync(string cookie, CancellationToken ct = default)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds) };
        var jar = new CookieJar(cookie);
        var basic = new BiliBasicInfoCrawler(http, jar);
        var account = new BilibiliAccount(basic);
        await account.InitializeAsync(ct);
        var live = new BiliLiveCrawler(http, account, jar);
        var ws = new WebsocketClient();
        return new DanmakuClient(ws, http, account, live);
    }
}
