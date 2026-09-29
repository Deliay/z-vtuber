using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Emotion;

public interface IEmotionTaskLockProvider
{
    Task<IEmotionTaskLock?> TryAcquireAsync(ObjectId taskId, CancellationToken ct = default);

    /// <summary>锁 key 是否仍被持有（用于判定 RUNNING 任务是否需要接管）。</summary>
    Task<bool> IsHeldAsync(ObjectId taskId, CancellationToken ct = default);
}

public interface IEmotionTaskLock : IAsyncDisposable
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
/// 情绪任务 Redis 锁：SET NX PX + 周期续期 + Lua token 比对释放 + fencing 校验。
/// key 约定 <c>training:emotion-task-lock:{taskId}</c>。语义逐条仿 recording-transcript-consumer 的
/// <c>RedisTranscriptTaskLockProvider</c>（含 Redis 抖动时「保守按持有/按失锁」的降级口径）。
/// </summary>
public sealed class RedisEmotionTaskLockProvider(
    IConnectionMultiplexer redis,
    EmotionTaskOptions options,
    ILogger<RedisEmotionTaskLockProvider> logger) : IEmotionTaskLockProvider
{
    public static string KeyFor(ObjectId taskId) => $"training:emotion-task-lock:{taskId}";

    public async Task<IEmotionTaskLock?> TryAcquireAsync(ObjectId taskId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var key = KeyFor(taskId);
        var token = Guid.NewGuid().ToString("N");
        var ttl = TimeSpan.FromSeconds(options.LockTtlSeconds);

        var acquired = await db.StringSetAsync(key, token, ttl, When.NotExists);
        if (!acquired)
        {
            logger.LogDebug("Emotion task lock {Key} held by another instance", key);
            return null;
        }

        logger.LogDebug("Acquired emotion task lock {Key} with token {Token}", key, token);
        return new RedisEmotionTaskLock(db, key, taskId, token, options, logger);
    }

    public async Task<bool> IsHeldAsync(ObjectId taskId, CancellationToken ct = default)
    {
        try
        {
            var db = redis.GetDatabase();
            return await db.KeyExistsAsync(KeyFor(taskId));
        }
        catch (Exception ex)
        {
            // Redis 抖动时无法判定：保守按「仍被持有」处理，避免误抢导致双跑
            logger.LogWarning(ex, "Failed to check emotion task lock {Key}; assuming held", KeyFor(taskId));
            return true;
        }
    }

    /// <summary>internal 以便测试注入不可用 Redis 验证 fencing 降级；生产仅经 provider 创建。</summary>
    internal sealed class RedisEmotionTaskLock(
        IDatabase db,
        string key,
        ObjectId taskId,
        string token,
        EmotionTaskOptions options,
        ILogger logger) : IEmotionTaskLock
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
            RedisValue current;
            try
            {
                current = await db.StringGetAsync(key);
            }
            catch (Exception ex)
            {
                // Redis 抖动不能判定为「任务失败」：按失锁处理，保留 RUNNING 交他节点接管
                logger.LogWarning(ex, "Emotion task lock {Key} ownership check failed; treating as lost", key);
                Volatile.Write(ref _lost, 1);
                return false;
            }

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
                logger.LogWarning("Emotion task lock {Key} lost during renewal", key);
            }

            return renewed;
        }

        public async Task ReleaseAsync(CancellationToken ct = default)
        {
            await db.ScriptEvaluateAsync(ReleaseScript, [key], [Token]);
            logger.LogDebug("Released emotion task lock {Key}", key);
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
                    logger.LogError(ex, "Emotion task lock renewal loop failed for {Key}", key);
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
