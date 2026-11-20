using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

public sealed class DanmakuDeduplicatorTests
{
    [Fact]
    public void DuplicateEvent_IsProcessedOnce()
    {
        var clock = new AuthTestClock();
        var dedup = new DanmakuDeduplicator(clock: clock);
        var occurredAt = clock.GetUtcNow().UtcDateTime;

        Assert.True(dedup.TryAdd("30066", occurredAt, 403496, "A1b2C3d4E5"));
        Assert.False(dedup.TryAdd("30066", occurredAt, 403496, "A1b2C3d4E5"));
        Assert.True(dedup.TryAdd("12345", occurredAt, 403496, "A1b2C3d4E5"));
    }

    [Fact]
    public void FailedProcessing_CanBeRetried()
    {
        var clock = new AuthTestClock();
        var dedup = new DanmakuDeduplicator(clock: clock);
        var occurredAt = clock.GetUtcNow().UtcDateTime;

        Assert.True(dedup.TryAdd("30066", occurredAt, 1, "token"));
        dedup.Remove("30066", occurredAt, 1, "token");
        Assert.True(dedup.TryAdd("30066", occurredAt, 1, "token"));
    }

    [Fact]
    public void WindowExpiry_AllowsSameEventAgain()
    {
        var clock = new AuthTestClock();
        var dedup = new DanmakuDeduplicator(window: TimeSpan.FromMinutes(10), clock: clock);
        var occurredAt = clock.GetUtcNow().UtcDateTime;

        Assert.True(dedup.TryAdd("30066", occurredAt, 1, "token"));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(dedup.TryAdd("30066", occurredAt, 1, "token"));
    }
}
