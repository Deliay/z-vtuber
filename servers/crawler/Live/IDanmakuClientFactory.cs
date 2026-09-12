using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Crawler.Live;

public interface IDanmakuClientFactory
{
    Task<IDanmakuClient> CreateAsync(string cookie, CancellationToken ct = default);
    Task<(IDanmakuClient Client, IDistributedLockHandle Release)> CreateWithLockAsync(string cookie, CancellationToken ct = default);
}
