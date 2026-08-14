using System.Text.Json;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Configuration;

namespace Z.Vtuber.Crawlers;

public sealed class BiliLiveCrawlerWrapper : BiliLiveCrawler
{
    public BiliLiveCrawlerWrapper(HttpClient client, BilibiliAccount account, CookieJar cookieJar)
        : base(client, account, cookieJar) { }

    public new ValueTask<T?> GetAsync<T>(string url, CancellationToken ct = default) =>
        base.GetAsync<T>(url, ct);
}
