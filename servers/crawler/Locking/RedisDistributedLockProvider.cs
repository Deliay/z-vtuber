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
        var lockKey = $"lock:{key}";
        var lockValue = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + waitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var acquired = await db.StringSetAsync(lockKey, lockValue, ttl, When.NotExists);
            if (acquired)
            {
                _logger.LogDebug("Acquired distributed lock {Key} with TTL {Ttl}.", key, ttl);
                return new RedisLockHandle(db, lockKey, lockValue, _logger);
            }

            // Wait before retrying
            var remaining = deadline - DateTime.UtcNow;
            var delay = TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds));
            await Task.Delay(delay, ct);
        }

        _logger.LogDebug("Failed to acquire distributed lock {Key} within {Timeout}.", key, waitTimeout);
        return null;
    }

    private sealed class RedisLockHandle : IDistributedLockHandle
    {
        private readonly IDatabase _db;
        private readonly string _key;
        private readonly string _value;
        private readonly ILogger _logger;
        private bool _disposed;

        public RedisLockHandle(IDatabase db, string key, string value, ILogger logger)
        {
            _db = db;
            _key = key;
            _value = value;
            _logger = logger;
        }

        public string Key => _key;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            // Release lock only if we own it
            var currentValue = await _db.StringGetAsync(_key);
            if (currentValue == _value)
            {
                await _db.KeyDeleteAsync(_key);
                _logger.LogDebug("Released distributed lock {Key}.", _key);
            }
        }
    }
}
