using System.Collections.Concurrent;

namespace Z.Vtuber.Crawler.Services;

/// <summary>
/// Per-cookie keyed lock ensuring that concurrent callers for the same cookie
/// serialize through a single semaphore. No eviction — entries are permanent.
/// </summary>
public sealed class CookieCallGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private SemaphoreSlim GetOrAdd(string cookie) =>
        _gates.GetOrAdd(cookie, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Acquires the lock for <paramref name="cookie"/>. Returns an <see cref="IDisposable"/>
    /// handle that releases the lock when disposed. Caller MUST dispose the handle
    /// when the guarded section completes (including on exception paths).
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string cookie, CancellationToken ct = default)
    {
        var sem = GetOrAdd(cookie);
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            semaphore.Release();
        }
    }
}
