using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>Phase 8.1：Redis 固定窗口计数器与 fail-open。</summary>
[Collection("redis")]
public sealed class AuthRedisRateLimiterTests
{
    private readonly RedisFixture _redis;

    public AuthRedisRateLimiterTests(RedisFixture redis) => _redis = redis;

    private static AuthOptions Options(int limit = 2) => new()
    {
        TokenPepper = AuthTestOptions.Pepper,
        RateLimit = new AuthRateLimitOptions
        {
            TokenPerMinutePerIp = limit,
            TokenPerDayPerIp = limit,
            TokenPerHourPerClient = limit,
            DanmakuPerMinutePerUid = limit,
            OAuthTokenPerMinutePerClient = limit
        }
    };

    private RedisRateLimiter Create(IConnectionMultiplexer connection, int limit = 2) =>
        new(connection, AuthTestOptions.Opt(Options(limit)), new AuthTestClock(),
            NullLogger<RedisRateLimiter>.Instance);

    [Fact]
    public async Task Counters_AllowUntilLimit_ThenReject()
    {
        var limiter = Create(_redis.Connection);
        var ip = "ip-" + Guid.NewGuid().ToString("N");

        Assert.True(await limiter.TryTokenPerMinuteAsync(ip, default));
        Assert.True(await limiter.TryTokenPerMinuteAsync(ip, default));
        Assert.False(await limiter.TryTokenPerMinuteAsync(ip, default));
    }

    [Fact]
    public async Task OAuthKey_IsScopedByClientAndIp()
    {
        var limiter = Create(_redis.Connection);
        var client = "client-" + Guid.NewGuid().ToString("N");

        Assert.True(await limiter.TryOAuthTokenAsync(client, "10.0.0.1", default));
        Assert.True(await limiter.TryOAuthTokenAsync(client, "10.0.0.1", default));
        Assert.False(await limiter.TryOAuthTokenAsync(client, "10.0.0.1", default));
        // 不同 IP 使用独立固定窗口，不被前一个 IP 拖垮。
        Assert.True(await limiter.TryOAuthTokenAsync(client, "10.0.0.2", default));
    }

    [Fact]
    public async Task RedisFailure_FailsOpen()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.SyncTimeout = 200;
        options.ConnectRetry = 0;
        await using var broken = await ConnectionMultiplexer.ConnectAsync(options);

        var limiter = Create(broken);
        Assert.True(await limiter.TryTokenPerMinuteAsync("degraded", default));
    }
}
