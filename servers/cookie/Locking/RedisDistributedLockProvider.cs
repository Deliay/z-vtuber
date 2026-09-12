using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Cookie.Locking;

public class RedisDistributedLockProvider : IDistributedLockProvider
{
    public Task<IDistributedLockHandle?> AcquireAsync(string key, TimeSpan ttl, TimeSpan waitTimeout, CancellationToken ct = default)
    {
        // TODO: implement Redis distributed lock
        return Task.FromResult<IDistributedLockHandle?>(null);
    }
}