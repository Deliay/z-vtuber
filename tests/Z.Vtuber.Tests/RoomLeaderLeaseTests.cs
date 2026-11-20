using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public sealed class RoomLeaderLeaseTests
{
    private readonly RedisFixture _redis;

    public RoomLeaderLeaseTests(RedisFixture redis) => _redis = redis;

    private RoomLeaderLease Create(IEnumerable<string>? rooms = null)
    {
        var auth = AuthTestOptions.Auth();
        var danmaku = AuthTestOptions.Danmaku(rooms?.ToArray() ?? [Guid.NewGuid().ToString("N")]);
        return new RoomLeaderLease(
            _redis.Connection,
            AuthTestOptions.Opt(auth),
            AuthTestOptions.Opt(danmaku),
            NullLogger<RoomLeaderLease>.Instance);
    }

    [Fact]
    public async Task Acquire_Renew_Release()
    {
        var room = "r" + Guid.NewGuid().ToString("N");
        var lease = Create();
        Assert.True(await lease.TryAcquireAsync(room, default));
        Assert.True(await lease.RenewAsync(room, default));
        Assert.True(await lease.IsHeldByOtherAsync(room, default));
        await lease.ReleaseAsync(room, default);
        Assert.False(await lease.IsHeldByOtherAsync(room, default));
    }

    [Fact]
    public async Task SecondInstance_IsStandbyUntilRelease()
    {
        var room = "r" + Guid.NewGuid().ToString("N");
        var first = Create();
        var second = Create();

        Assert.True(await first.TryAcquireAsync(room, default));
        Assert.False(await second.TryAcquireAsync(room, default));
        Assert.True(await second.IsHeldByOtherAsync(room, default));

        await first.ReleaseAsync(room, default);
        Assert.True(await second.TryAcquireAsync(room, default));
        await second.ReleaseAsync(room, default);
    }

    [Fact]
    public async Task DegradedMode_WithoutRedis_IsAlwaysLeader()
    {
        var lease = new RoomLeaderLease(
            null,
            AuthTestOptions.Opt(AuthTestOptions.Auth()),
            AuthTestOptions.Opt(AuthTestOptions.Danmaku()),
            NullLogger<RoomLeaderLease>.Instance);

        Assert.True(await lease.TryAcquireAsync("30066", default));
        Assert.True(await lease.RenewAsync("30066", default));
    }

    [Fact]
    public async Task NonDegradedMode_WithoutRedis_IsNotLeader()
    {
        var auth = AuthTestOptions.Auth();
        auth.AllowDegradedRedis = false;
        var lease = new RoomLeaderLease(
            null,
            AuthTestOptions.Opt(auth),
            AuthTestOptions.Opt(AuthTestOptions.Danmaku()),
            NullLogger<RoomLeaderLease>.Instance);

        Assert.False(await lease.TryAcquireAsync("30066", default));
    }

}
