using System.Collections.Concurrent;
using StackExchange.Redis;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;

namespace Z.Vtuber.Auth.Danmaku;

public interface IRoomLeaderLease
{
    Task<bool> TryAcquireAsync(string roomId, CancellationToken ct);

    Task<bool> RenewAsync(string roomId, CancellationToken ct);

    Task ReleaseAsync(string roomId, CancellationToken ct);

    Task<bool> IsHeldByOtherAsync(string roomId, CancellationToken ct);
}

/// <summary>
/// 每房间 Redis 选主租约：<c>SET NX PX</c> 获取、Lua 比较续期/释放。
/// Redis 不可用时按 <see cref="AuthOptions.AllowDegradedRedis"/> 决定是否所有实例都订阅。
/// </summary>
public sealed class RoomLeaderLease(
    IConnectionMultiplexer? redis,
    IOptions<AuthOptions> authOptions,
    IOptions<DanmakuOptions> danmakuOptions,
    ILogger<RoomLeaderLease> logger) : IRoomLeaderLease
{
    private const string RenewScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public async Task<bool> TryAcquireAsync(string roomId, CancellationToken ct)
    {
        if (redis is null)
        {
            return Degraded();
        }

        try
        {
            var token = $"{_instanceId}:{Guid.NewGuid():N}";
            var acquired = await redis.GetDatabase().StringSetAsync(
                Key(roomId),
                token,
                TimeSpan.FromSeconds(danmakuOptions.Value.LeaderLockTtlSeconds),
                When.NotExists);
            if (acquired)
            {
                _tokens[roomId] = token;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis leader lease acquire failed for room {RoomId}.", roomId);
            return Degraded();
        }
    }

    public async Task<bool> RenewAsync(string roomId, CancellationToken ct)
    {
        if (redis is null)
        {
            return Degraded();
        }

        if (!_tokens.TryGetValue(roomId, out var token))
        {
            return false;
        }

        try
        {
            var renewed = (long)await redis.GetDatabase().ScriptEvaluateAsync(
                RenewScript,
                [Key(roomId)],
                [token, danmakuOptions.Value.LeaderLockTtlSeconds * 1000]);
            if (renewed == 1)
            {
                return true;
            }

            _tokens.TryRemove(roomId, out _);
            return false;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis leader lease renew failed for room {RoomId}.", roomId);
            return Degraded();
        }
    }

    public async Task ReleaseAsync(string roomId, CancellationToken ct)
    {
        if (!_tokens.TryRemove(roomId, out var token) || redis is null)
        {
            return;
        }

        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [Key(roomId)], [token]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis leader lease release failed for room {RoomId}.", roomId);
        }
    }

    public async Task<bool> IsHeldByOtherAsync(string roomId, CancellationToken ct)
    {
        if (redis is null)
        {
            return false;
        }

        try
        {
            return await redis.GetDatabase().KeyExistsAsync(Key(roomId));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogDebug(ex, "Redis leader lease inspect failed for room {RoomId}.", roomId);
            return false;
        }
    }

    public static string Key(string roomId) => $"auth:danmaku:leader:{roomId}";

    private bool Degraded() => authOptions.Value.AllowDegradedRedis;
}

/// <summary>无 Redis 的单实例/测试租约：始终为 leader。</summary>
public sealed class AlwaysLeaderRoomLease : IRoomLeaderLease
{
    public Task<bool> TryAcquireAsync(string roomId, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> RenewAsync(string roomId, CancellationToken ct) => Task.FromResult(true);

    public Task ReleaseAsync(string roomId, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> IsHeldByOtherAsync(string roomId, CancellationToken ct) => Task.FromResult(false);
}
