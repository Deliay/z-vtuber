using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.WebsocketCrawler.Client;
using Z.Vtuber.Crawler.Crawlers;

namespace Z.Vtuber.Crawler.Live;

public sealed class DanmakuClientFactory(IBilibiliAccountProvider accountProvider)
{
    public async Task<DanmakuClient> CreateAsync(string cookie, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var http = provider.GetRequiredService<HttpClient>();
        var account = provider.GetRequiredService<BilibiliAccount>();
        var live = provider.GetRequiredService<BiliLiveCrawler>();
        var ws = new WebsocketClient();
        return new DanmakuClient(ws, http, account, live);
    }
}
