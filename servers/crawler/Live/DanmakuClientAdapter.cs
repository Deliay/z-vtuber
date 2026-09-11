using Mikibot.Crawler.WebsocketCrawler.Client;
using Mikibot.Crawler.WebsocketCrawler.Data;

namespace Z.Vtuber.Crawler.Live;

public sealed class DanmakuClientAdapter(DanmakuClient inner) : IDanmakuClient
{
    public ValueTask<bool> ConnectAsync(long roomId, CancellationToken ct = default)
        => inner.ConnectAsync(roomId, ct);

    public IAsyncEnumerable<IData> Events(CancellationToken ct = default)
        => inner.Events(ct);

    public void Dispose() => inner.Dispose();
}
