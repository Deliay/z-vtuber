namespace Z.Vtuber.Tests;

/// <summary>
/// 轮询断言工具：以短间隔反复求值条件，直到满足或总超时。
/// 用于替代硬编码 <c>Task.Delay</c>，消除「等太短则 flaky、等太长则慢」的问题。
/// 统一参数：默认 50ms 间隔、5s 总超时（超时消息带条件描述便于定位）。
/// </summary>
internal static class TestWait
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>轮询直到同步条件成立；超时直接失败（不再重复求值，避免副作用被触发两次）。</summary>
    public static Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, TimeSpan? interval = null, string? describe = null)
        => UntilAsync(() => Task.FromResult(condition()), timeout, interval, describe);

    /// <summary>轮询直到异步条件成立；超时直接失败（不再重复求值，避免副作用被触发两次）。</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, TimeSpan? interval = null, string? describe = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var effectiveInterval = interval ?? DefaultInterval;
        var deadline = DateTime.UtcNow + effectiveTimeout;

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(effectiveInterval);
        }

        Assert.Fail(describe is null
            ? $"Condition was not met within {effectiveTimeout}."
            : $"Condition '{describe}' was not met within {effectiveTimeout}.");
    }

    /// <summary>
    /// 断言条件在给定时长内始终不成立：用于验证定时器/锁/退订不会提前触发。
    /// 条件一旦成立立即失败（而非等到时长结束）。
    /// </summary>
    public static Task NeverAsync(Func<bool> condition, TimeSpan duration, TimeSpan? interval = null, string? describe = null)
        => NeverAsync(() => Task.FromResult(condition()), duration, interval, describe);

    /// <inheritdoc cref="NeverAsync(Func{bool}, TimeSpan, TimeSpan?, string?)"/>
    public static async Task NeverAsync(Func<Task<bool>> condition, TimeSpan duration, TimeSpan? interval = null, string? describe = null)
    {
        var effectiveInterval = interval ?? DefaultInterval;
        var deadline = DateTime.UtcNow + duration;

        while (true)
        {
            Assert.False(await condition(), describe is null
                ? "Condition unexpectedly became true."
                : $"Condition '{describe}' unexpectedly became true.");

            if (DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(effectiveInterval);
        }
    }
}
