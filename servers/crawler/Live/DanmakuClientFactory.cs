using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.WebsocketCrawler.Client;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Crawler.Live;

public class DanmakuClientFactory(IBilibiliAccountProvider accountProvider, CookieCallGate gate) : IDanmakuClientFactory
{
    public async Task<IDanmakuClient> CreateAsync(string cookieId, string cookie, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookieId, cookie, ct);
        return BuildClient(provider);
    }

    /// <summary>
    /// Lock-aware variant: acquires the cookie lock, calls the lock-free provider core,
    /// and returns the client together with a release handle that MUST be disposed
    /// by the caller after the websocket connection is established.
    /// </summary>
    public async Task<(IDanmakuClient Client, IDistributedLockHandle Release)> CreateWithLockAsync(string cookieId, string cookie, CancellationToken ct = default)
    {
        var releaser = await gate.AcquireAsync(cookieId, ct);
        try
        {
            // Cast to BilibiliAccountProvider to access the lock-free internal core.
            // The lock is already held by the caller (LiveEventSource).
            var provider = (BilibiliAccountProvider)accountProvider;
            var sp = await provider.GetProviderInternalAsync(cookieId, cookie, ct);
            var client = BuildClient(sp);
            return (client, releaser);
        }
        catch
        {
            await releaser.DisposeAsync();
            throw;
        }
    }

    private static IDanmakuClient BuildClient(IServiceProvider provider)
    {
        var http = provider.GetRequiredService<HttpClient>();
        var account = provider.GetRequiredService<BilibiliAccount>();
        var live = provider.GetRequiredService<BiliLiveCrawler>();
        var ws = new WebsocketClient();
        return new DanmakuClientAdapter(new DanmakuClient(ws, http, account, live));
    }
}
