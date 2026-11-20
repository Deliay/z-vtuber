using StackExchange.Redis;
using Z.Vtuber.Auth.Configuration;

namespace Z.Vtuber.Auth.Auth;

public interface IAuthRateLimiter
{
    Task<bool> TryTokenPerMinuteAsync(string clientIp, CancellationToken ct);

    Task<bool> TryTokenPerDayAsync(string clientIp, CancellationToken ct);

    Task<bool> TryTokenPerClientAsync(string clientId, CancellationToken ct);

    Task<bool> TryDanmakuUidAsync(long uid, CancellationToken ct);

    Task<bool> TryOAuthTokenAsync(string clientId, string clientIp, CancellationToken ct);
}

/// <summary>
/// Redis 固定窗口限流（Lua INCR + PEXPIRE）；Redis 不可用 fail-open 并仅记 warning。
/// </summary>
public sealed class RedisRateLimiter(
    IConnectionMultiplexer? redis,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<RedisRateLimiter> logger) : IAuthRateLimiter
{
    private const string Script =
        "local c = redis.call('incr', KEYS[1]); " +
        "if c == 1 then redis.call('pexpire', KEYS[1], ARGV[1]); end; " +
        "if c > tonumber(ARGV[2]) then return 0 else return 1 end";

    public Task<bool> TryTokenPerMinuteAsync(string clientIp, CancellationToken ct) =>
        AllowAsync($"auth:rl:token:ip:{Normalize(clientIp)}:{Minute()}", options.Value.RateLimit.TokenPerMinutePerIp, 120_000, ct);

    public Task<bool> TryTokenPerDayAsync(string clientIp, CancellationToken ct) =>
        AllowAsync($"auth:rl:token:ip:day:{Normalize(clientIp)}:{Day()}", options.Value.RateLimit.TokenPerDayPerIp, 86_400_000, ct);

    public Task<bool> TryTokenPerClientAsync(string clientId, CancellationToken ct) =>
        AllowAsync($"auth:rl:token:client:{Normalize(clientId)}:{Hour()}", options.Value.RateLimit.TokenPerHourPerClient, 7_200_000, ct);

    public Task<bool> TryDanmakuUidAsync(long uid, CancellationToken ct) =>
        AllowAsync($"auth:rl:danmaku:uid:{uid}:{Minute()}", options.Value.RateLimit.DanmakuPerMinutePerUid, 120_000, ct);

    public Task<bool> TryOAuthTokenAsync(string clientId, string clientIp, CancellationToken ct) =>
        AllowAsync(
            $"auth:oauth:rl:client:{Normalize(clientId)}:ip:{Normalize(clientIp)}:{Minute()}",
            options.Value.RateLimit.OAuthTokenPerMinutePerClient,
            120_000,
            ct);

    public async Task<bool> AllowAsync(string key, int limit, int windowMs, CancellationToken ct)
    {
        if (redis is null)
        {
            return true;
        }

        try
        {
            var result = (long)await redis.GetDatabase().ScriptEvaluateAsync(
                Script, [key], [windowMs, limit]);
            return result == 1;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis rate limiter unavailable; failing open for key {Key}.", key);
            return true;
        }
    }

    private string Minute() => clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmm");
    private string Hour() => clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddHH");
    private string Day() => clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd");

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace(':', '_');
}
