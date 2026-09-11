using Mikibot.Crawler.WebsocketCrawler.Data;

namespace Z.Vtuber.Crawler.Live;

public interface IDanmakuClient : IDisposable
{
    ValueTask<bool> ConnectAsync(long roomId, CancellationToken ct = default);
    IAsyncEnumerable<IData> Events(CancellationToken ct = default);
}
