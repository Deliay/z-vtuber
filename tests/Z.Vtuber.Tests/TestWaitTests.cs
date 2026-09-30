using System.Diagnostics;
using Xunit.Sdk;

namespace Z.Vtuber.Tests;

/// <summary><see cref="TestWait"/> 的行为测试：成功、异步谓词、超时、间隔生效。</summary>
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
}
