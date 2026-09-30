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

    public static Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, TimeSpan? interval = null)
        => UntilAsync(() => Task.FromResult(condition()), timeout, interval);

    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, TimeSpan? interval = null)
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

        Assert.True(await condition(), $"Condition was not met within {effectiveTimeout}.");
    }
}
