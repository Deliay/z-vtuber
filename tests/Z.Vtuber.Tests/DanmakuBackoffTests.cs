using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

public sealed class DanmakuBackoffTests
{
    [Fact]
    public void Backoff_IsCappedAndJittered()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), DanmakuBackoff.Next(1, 30));
        Assert.Equal(TimeSpan.FromSeconds(2), DanmakuBackoff.Next(2, 30));
        Assert.Equal(TimeSpan.FromSeconds(4), DanmakuBackoff.Next(3, 30));
        Assert.Equal(TimeSpan.FromSeconds(8), DanmakuBackoff.Next(4, 30));
        Assert.Equal(TimeSpan.FromSeconds(16), DanmakuBackoff.Next(5, 30));
        Assert.Equal(TimeSpan.FromSeconds(30), DanmakuBackoff.Next(9, 30));
        Assert.True(DanmakuBackoff.Next(9, 30, 1) > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void StableRun_ResetsAttemptCounter_RegardlessOfExitPath()
    {
        var threshold = TimeSpan.FromSeconds(60);

        // 稳定运行达到阈值 → 重置为 1（即使上一次失败计数已到上限）。
        Assert.Equal(1, DanmakuBackoff.NextAttempt(5, TimeSpan.FromSeconds(61), threshold));

        // 未达阈值（读超时/异常路径常见）→ 累加。
        Assert.Equal(6, DanmakuBackoff.NextAttempt(5, TimeSpan.FromSeconds(59), threshold));
    }
}
