using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Crawler.Services;

/// <summary>
/// Per-cookie keyed lock ensuring that concurrent callers for the same cookie
/// serialize through a distributed lock. No eviction — entries are permanent.
/// </summary>
public sealed class CookieCallGate
{
    private readonly IDistributedLockProvider _lockProvider;

    public CookieCallGate(IDistributedLockProvider lockProvider)
    {
        _lockProvider = lockProvider;
    }

    /// <summary>
    /// Acquires the lock for <paramref name="cookie"/>. Returns an <see cref="IDistributedLockHandle"/>
    /// handle that releases the lock when disposed. Caller MUST dispose the handle
    /// when the guarded section completes (including on exception paths).
    /// </summary>
    public async Task<IDistributedLockHandle> AcquireAsync(string cookieId, CancellationToken ct = default)
    {
        var key = $"cookie-call:{cookieId}";
        var handle = await _lockProvider.AcquireAsync(key, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), ct);
        if (handle is null)
        {
            throw new TimeoutException($"Failed to acquire cookie lock for {cookieId} within timeout.");
        }
        return handle;
    }
}
