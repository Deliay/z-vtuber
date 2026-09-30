using System.Diagnostics;
using Xunit.Sdk;

namespace Z.Vtuber.Tests;

/// <summary><see cref="TestWait"/> 的行为测试：成功、异步谓词、超时、间隔生效、NeverAsync。</summary>
public sealed class TestWaitTests
{
    [Fact]
    public async Task UntilAsync_ReturnsOnceConditionBecomesTrue()
    {
        var attempts = 0;
        await TestWait.UntilAsync(() => ++attempts >= 3, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));
        Assert.True(attempts >= 3);
    }

    [Fact]
    public async Task UntilAsync_SupportsAsyncPredicate()
    {
        var flag = false;
        var task = Task.Run(async () =>
        {
            await Task.Delay(30);
            flag = true;
        });

        await TestWait.UntilAsync(() => Task.FromResult(flag), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));
        await task;
        Assert.True(flag);
    }

    [Fact]
    public async Task UntilAsync_ThrowsWithDescriptiveMessageOnTimeout()
    {
        var exception = await Assert.ThrowsAsync<TrueException>(() =>
            TestWait.UntilAsync(() => false, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));

        Assert.Contains("Condition was not met", exception.Message);
    }

    [Fact]
    public async Task UntilAsync_DoesNotReevaluatePredicateOnTimeout()
    {
        // 超时兜底不得再次求值：timeout 50ms + interval 60ms → 恰好在 t=0 与 t=60 各求值一次。
        var calls = 0;
        await Assert.ThrowsAsync<TrueException>(() =>
            TestWait.UntilAsync(
                () =>
                {
                    calls++;
                    return false;
                },
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(60)));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UntilAsync_RespectsInterval()
    {
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TrueException>(() =>
            TestWait.UntilAsync(() => false, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(180),
            $"应至少等待接近总超时，实际 {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task UntilAsync_UsesDefaultTimeoutWhenOmitted()
    {
        var attempts = 0;
        await TestWait.UntilAsync(() => ++attempts >= 2);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task NeverAsync_ReturnsWhenConditionStaysFalse()
    {
        var stopwatch = Stopwatch.StartNew();
        await TestWait.NeverAsync(() => false, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(20));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task NeverAsync_FailsFastWhenConditionBecomesTrue()
    {
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<FalseException>(() =>
            TestWait.NeverAsync(() => stopwatch.ElapsedMilliseconds > 40, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"应在条件成立时立即失败，实际 {stopwatch.ElapsedMilliseconds}ms");
    }
}
