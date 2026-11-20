using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Z.Vtuber.Tests;

public sealed class DanmakuHealthRegistryTests
{
    private static (DanmakuHealthRegistry Registry, AuthTestClock Clock) Create()
    {
        var clock = new AuthTestClock();
        return (new DanmakuHealthRegistry(clock), clock);
    }

    [Fact]
    public void LeaderConnected_IsAvailableAndReady_EvenWithoutHeartbeat()
    {
        var (registry, _) = Create();
        registry.MarkLeader("30066");
        registry.MarkConnected("30066");

        var room = registry.GetSnapshots(["30066"]).Single();
        Assert.True(room.Connected);
        Assert.True(room.Available);
        Assert.True(registry.IsReady(["30066"], TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void LeaderDisconnected_WithRecentFrame_RemainsAvailableWithinGrace()
    {
        var (registry, clock) = Create();
        registry.MarkLeader("30066");
        registry.MarkConnected("30066");
        registry.MarkHeartbeat("30066");
        registry.MarkDisconnected("30066");

        Assert.False(registry.GetSnapshots(["30066"]).Single().Connected);
        Assert.True(registry.GetSnapshots(["30066"]).Single().Available);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(registry.GetSnapshots(["30066"]).Single().Available);
        Assert.False(registry.IsReady(["30066"], TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void FollowerWithObservedLease_IsAvailableAndReady()
    {
        var (registry, _) = Create();
        registry.MarkStandby("30066", otherInstanceLease: true);

        var room = registry.GetSnapshots(["30066"]).Single();
        Assert.Equal(RoomRoles.Standby, room.Role);
        Assert.True(room.Available);
        Assert.True(registry.IsReady(["30066"], TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void FollowerWithoutLease_IsNotAvailable()
    {
        var (registry, _) = Create();
        registry.MarkStandby("30066", otherInstanceLease: false);

        Assert.False(registry.GetSnapshots(["30066"]).Single().Available);
        Assert.False(registry.IsReady(["30066"], TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void UnknownRoom_IsDisconnectedAndUnavailable()
    {
        var (registry, _) = Create();
        var room = registry.GetSnapshots(["12345"]).Single();
        Assert.Equal(RoomRoles.Disconnected, room.Role);
        Assert.False(room.Available);
    }

    [Fact]
    public async Task ReadyHealthCheck_ReportsHealthyOnlyWhenSomeRoomIsAvailable()
    {
        var (registry, _) = Create();
        var options = AuthTestOptions.Opt(AuthTestOptions.Danmaku("30066", "12345"));
        var check = new DanmakuReadyHealthCheck(registry, options);

        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);

        registry.MarkLeader("12345");
        registry.MarkConnected("12345");
        var healthy = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
    }
}
