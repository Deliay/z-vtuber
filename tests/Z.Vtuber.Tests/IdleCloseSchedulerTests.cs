using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Cookie.Browser;

namespace Z.Vtuber.Tests;

public sealed class IdleCloseSchedulerTests
{
    [Fact]
    public async Task Touch_WhenDelayElapses_InvokesCloseOnce()
    {
        var closed = new ConcurrentQueue<string>();
        await using var scheduler = new IdleCloseScheduler(
            TimeSpan.FromMilliseconds(80),
            (key, _, _) => { closed.Enqueue(key); return Task.CompletedTask; },
            NullLogger.Instance);

        scheduler.Touch("account-a");

        await TestWait.UntilAsync(() => closed.Contains("account-a"), TimeSpan.FromSeconds(2));

        Assert.Equal(["account-a"], closed.ToArray());
    }

    [Fact]
    public async Task Touch_AgainBeforeElapse_DelaysClose()
    {
        var closed = new ConcurrentQueue<string>();
        await using var scheduler = new IdleCloseScheduler(
            TimeSpan.FromMilliseconds(150),
            (key, _, _) => { closed.Enqueue(key); return Task.CompletedTask; },
            NullLogger.Instance);

        scheduler.Touch("account-a");
        await Task.Delay(60);
        scheduler.Touch("account-a");

        // 原始 150ms 到期点已过、但重新 Touch 已顺延：不得触发关闭。
        await TestWait.NeverAsync(
            () => closed.Contains("account-a"),
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(15),
            "idle close must be deferred after re-touch");

        await TestWait.UntilAsync(() => closed.Contains("account-a"), TimeSpan.FromSeconds(3));
        Assert.Equal(["account-a"], closed.ToArray());
    }

    [Fact]
    public async Task Cancel_PreventsClose()
    {
        var closed = new ConcurrentQueue<string>();
        await using var scheduler = new IdleCloseScheduler(
            TimeSpan.FromMilliseconds(120),
            (key, _, _) => { closed.Enqueue(key); return Task.CompletedTask; },
            NullLogger.Instance);

        scheduler.Touch("account-a");
        scheduler.Cancel("account-a");

        await TestWait.NeverAsync(
            () => !closed.IsEmpty,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(15),
            "cancelled key must not be closed");
        Assert.Empty(closed);
    }

    [Fact]
    public async Task CloseCallbackThrows_DoesNotPropagateAndOtherKeysStillFire()
    {
        var closed = new ConcurrentQueue<string>();
        await using var scheduler = new IdleCloseScheduler(
            TimeSpan.FromMilliseconds(80),
            (key, _, _) =>
            {
                if (key == "bad")
                {
                    throw new InvalidOperationException("close failed");
                }

                closed.Enqueue(key);
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        scheduler.Touch("bad");
        scheduler.Touch("good");

        await TestWait.UntilAsync(() => closed.Contains("good"), TimeSpan.FromSeconds(2));
        Assert.Equal(["good"], closed.ToArray());
    }

    [Fact]
    public async Task MultipleKeys_AreIndependent()
    {
        var closed = new ConcurrentQueue<string>();
        await using var scheduler = new IdleCloseScheduler(
            TimeSpan.FromMilliseconds(80),
            (key, _, _) => { closed.Enqueue(key); return Task.CompletedTask; },
            NullLogger.Instance);

        scheduler.Touch("a");
        scheduler.Touch("b");
        scheduler.Touch("c");

        await TestWait.UntilAsync(() => closed.Count == 3, TimeSpan.FromSeconds(2));
        Assert.Equal(3, closed.Distinct().Count());
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingTimers()
    {
        var closed = new ConcurrentQueue<string>();
        var scheduler = new IdleCloseScheduler(
            TimeSpan.FromMilliseconds(200),
            (key, _, _) => { closed.Enqueue(key); return Task.CompletedTask; },
            NullLogger.Instance);

        scheduler.Touch("account-a");
        await scheduler.DisposeAsync();

        await TestWait.NeverAsync(
            () => !closed.IsEmpty,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(15),
            "disposed scheduler must not close");
        Assert.Empty(closed);
    }
}
