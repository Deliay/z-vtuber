using StackExchange.Redis;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Crawler.Locking;

public sealed class RedisDistributedLockProvider : IDistributedLockProvider
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLockProvider> _logger;

    public RedisDistributedLockProvider(IConnectionMultiplexer redis, ILogger<RedisDistributedLockProvider> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IDistributedLockHandle?> AcquireAsync(string key, TimeSpan ttl, TimeSpan waitTimeout, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var token = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + waitTimeout;
        var random = new Random();

        while (true)
        {
            if (await db.LockTakeAsync(key, token, ttl))
            {
                _logger.LogDebug("Acquired distributed lock {Key} with token {Token}", key, token);
                return new RedisLockHandle(db, key, token, _logger);
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogDebug("Failed to acquire distributed lock {Key} within timeout", key);
                return null;
            }

            var retryMs = random.Next(50, 201);
            await Task.Delay(retryMs, ct);
        }
    }

internal sealed class RedisLockHandle : IDistributedLockHandle
{
    private readonly IDatabase _db;
    private readonly ILogger _logger;
    private bool _disposed;

    public string Key { get; }
    public string Token { get; }

    public RedisLockHandle(IDatabase db, string key, string token, ILogger logger)
    {
        _db = db;
        Key = key;
        Token = token;
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only release if we still own the lock (token match)
        var acquired = await _db.LockReleaseAsync(Key, Token);
        if (acquired)
        {
            _logger.LogDebug("Released distributed lock {Key}", Key);
        }
        else
        {
            _logger.LogDebug("Lock {Key} already expired or owned by another holder", Key);
        }
    }
}
}
