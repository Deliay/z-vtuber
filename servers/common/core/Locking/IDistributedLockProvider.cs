namespace Z.Vtuber.Common.Locking;

public interface IDistributedLockProvider
{
    Task<IDistributedLockHandle?> AcquireAsync(string key, TimeSpan ttl, TimeSpan waitTimeout, CancellationToken ct = default);
}

public interface IDistributedLockHandle : IAsyncDisposable
{
    string Key { get; }
}