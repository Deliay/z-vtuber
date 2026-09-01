using System.Collections.Concurrent;

namespace Z.Vtuber.Crawler.Jobs;

public sealed class CrawlLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SemaphoreSlim GetLock(string key) => _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
}
