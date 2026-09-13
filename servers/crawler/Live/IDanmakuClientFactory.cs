using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Crawler.Live;

public interface IDanmakuClientFactory
{
    Task<IDanmakuClient> CreateAsync(string cookieId, string cookie, CancellationToken ct = default);
    Task<(IDanmakuClient Client, IDistributedLockHandle Release)> CreateWithLockAsync(string cookieId, string cookie, CancellationToken ct = default);
}
