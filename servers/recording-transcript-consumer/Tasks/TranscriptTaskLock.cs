using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

public interface ITranscriptTaskLockProvider
{
    Task<ITranscriptTaskLock?> TryAcquireAsync(ObjectId taskId, CancellationToken ct = default);

    /// <summary>锁 key 是否仍被持有（用于判定 RUNNING 任务是否需要接管）。</summary>
    Task<bool> IsHeldAsync(ObjectId taskId, CancellationToken ct = default);
}

public interface ITranscriptTaskLock : IAsyncDisposable
{
    ObjectId TaskId { get; }
    string Token { get; }
    bool Lost { get; }
    Task<bool> CheckOwnershipAsync(CancellationToken ct = default);
    Task<bool> RenewAsync(CancellationToken ct = default);
    Task ReleaseAsync(CancellationToken ct = default);
    void StartRenewal(CancellationToken ct);
}

/// <summary>
/// Redis 任务锁：SET NX PX + 周期续期 + Lua token 比对释放 + fencing 校验。
/// key 约定 transcript:task-lock:{taskId}。语义仿 recording 的 RedisRecordingLockProvider。
/// </summary>
public sealed class RedisTranscriptTaskLockProvider(
    IConnectionMultiplexer redis,
    TranscriptConsumerOptions options,
    ILogger<RedisTranscriptTaskLockProvider> logger) : ITranscriptTaskLockProvider
{
    public static string KeyFor(ObjectId taskId) => $"transcript:task-lock:{taskId}";

    public async Task<ITranscriptTaskLock?> TryAcquireAsync(ObjectId taskId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var key = KeyFor(taskId);
        var token = Guid.NewGuid().ToString("N");
        var ttl = TimeSpan.FromSeconds(options.LockTtlSeconds);

        var acquired = await db.StringSetAsync(key, token, ttl, When.NotExists);
        if (!acquired)
        {
            logger.LogDebug("Transcript task lock {Key} held by another instance", key);
            return null;
        }

        logger.LogDebug("Acquired transcript task lock {Key} with token {Token}", key, token);
        return new RedisTranscriptTaskLock(db, key, taskId, token, options, logger);
    }

    public async Task<bool> IsHeldAsync(ObjectId taskId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        return await db.KeyExistsAsync(KeyFor(taskId));
    }

    private sealed class RedisTranscriptTaskLock(
        IDatabase db,
        string key,
        ObjectId taskId,
        string token,
        TranscriptConsumerOptions options,
        ILogger logger) : ITranscriptTaskLock
    {
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        private const string RenewScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenSource? _renewalCts;
        private int _lost;

        public ObjectId TaskId { get; } = taskId;
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
            var result = (long)await db.ScriptEvaluateAsync(RenewScript, [key], [Token, ttlMs]);
            var renewed = result == 1;
            if (!renewed)
            {
                Volatile.Write(ref _lost, 1);
                logger.LogWarning("Transcript task lock {Key} lost during renewal", key);
            }

            return renewed;
        }

        public async Task ReleaseAsync(CancellationToken ct = default)
        {
            await db.ScriptEvaluateAsync(ReleaseScript, [key], [Token]);
            logger.LogDebug("Released transcript task lock {Key}", key);
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
                    logger.LogError(ex, "Transcript task lock renewal loop failed for {Key}", key);
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
