using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Crawler.Jobs;

public sealed class CrawlLockProvider
{
    private readonly IDistributedLockProvider _lockProvider;

    public CrawlLockProvider(IDistributedLockProvider lockProvider)
    {
        _lockProvider = lockProvider;
    }

    public async Task<IDistributedLockHandle> AcquireAsync(string key, CancellationToken ct = default)
    {
        var lockKey = $"crawl:{key}";
        var handle = await _lockProvider.AcquireAsync(lockKey, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), ct);
        if (handle is null)
        {
            throw new TimeoutException($"Failed to acquire crawl lock for {key} within timeout.");
        }
        return handle;
    }
}
