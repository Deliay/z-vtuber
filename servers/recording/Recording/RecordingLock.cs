using StackExchange.Redis;
using Z.Vtuber.Recording.Configuration;

namespace Z.Vtuber.Recording.Recording;

public interface IRecordingLockProvider
{
    Task<IRecordingLock?> TryAcquireAsync(long roomId, CancellationToken ct = default);
}

public interface IRecordingLock : IAsyncDisposable
{
    long RoomId { get; }
    string Token { get; }
    bool Lost { get; }
    Task<bool> CheckOwnershipAsync(CancellationToken ct = default);
    Task<bool> RenewAsync(CancellationToken ct = default);
    Task ReleaseAsync(CancellationToken ct = default);
    void StartRenewal(CancellationToken ct);
}

/// <summary>
/// Redis per-room 录制锁：SET NX PX + 周期续期 + Lua token 比对释放 + fencing 校验。
/// key 约定 recording:lock:{roomId}。
/// </summary>
public sealed class RedisRecordingLockProvider(
    IConnectionMultiplexer redis,
    RecordingOptions options,
    ILogger<RedisRecordingLockProvider> logger) : IRecordingLockProvider
{
    public static string KeyFor(long roomId) => $"recording:lock:{roomId}";

    public async Task<IRecordingLock?> TryAcquireAsync(long roomId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var key = KeyFor(roomId);
        var token = Guid.NewGuid().ToString("N");
        var ttl = TimeSpan.FromSeconds(options.LockTtlSeconds);

        var acquired = await db.StringSetAsync(key, token, ttl, When.NotExists);
        if (!acquired)
        {
            logger.LogDebug("Recording lock {Key} held by another instance", key);
            return null;
        }

        logger.LogDebug("Acquired recording lock {Key} with token {Token}", key, token);
        return new RedisRecordingLock(db, key, roomId, token, options, logger);
    }

    private sealed class RedisRecordingLock(
        IDatabase db,
        string key,
        long roomId,
        string token,
        RecordingOptions options,
        ILogger logger) : IRecordingLock
    {
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        private const string RenewScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _renewalCts;
    private int _lost;

        public long RoomId { get; } = roomId;
        public string Token { get; } = token;
        public bool Lost => Volatile.Read(ref _lost) == 1;

        public async Task<bool> CheckOwnershipAsync(CancellationToken ct = default)
        {
            var current = await db.StringGetAsync(key);
            var owned = current == Token;
            if (!owned)
            {
                Volatile.Write(ref _lost, 1);
            }

            return owned;
        }

        public async Task<bool> RenewAsync(CancellationToken ct = default)
        {
            var ttlMs = (long)TimeSpan.FromSeconds(options.LockTtlSeconds).TotalMilliseconds;
            var result = (long)await db.ScriptEvaluateAsync(
                RenewScript,
                [key],
                [Token, ttlMs]);
            var renewed = result == 1;
            if (!renewed)
            {
                Volatile.Write(ref _lost, 1);
                logger.LogWarning("Recording lock {Key} lost during renewal", key);
            }

            return renewed;
        }

        public async Task ReleaseAsync(CancellationToken ct = default)
        {
            await db.ScriptEvaluateAsync(ReleaseScript, [key], [Token]);
            logger.LogDebug("Released recording lock {Key}", key);
        }

        public void StartRenewal(CancellationToken ct)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            _renewalCts = linked;
            _ = Task.Run(async () =>
            {
                var interval = TimeSpan.FromSeconds(options.LockRenewIntervalSeconds);
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(linked.Token))
                    {
                        if (!await RenewAsync(linked.Token))
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常停机
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Recording lock renewal loop failed for {Key}", key);
                }
            }, linked.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _renewalCts?.Dispose();
        }
    }
}
