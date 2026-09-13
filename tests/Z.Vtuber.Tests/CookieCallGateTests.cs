using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

public class CookieCallGateTests
{
    private static CookieCallGate CreateGate() => new(new FakeDistributedLockProvider());

    [Fact]
    public async Task AcquireAsync_ConcurrentCallsForSameCookie_AreSerialized()
    {
        var gate = CreateGate();
        var order = new List<int>();
        var holder = await gate.AcquireAsync("cookie1");

        var waiterTask = Task.Run(async () =>
        {
            await using var _ = await gate.AcquireAsync("cookie1");
            order.Add(2);
        });

        await Task.Delay(100);
        Assert.False(waiterTask.IsCompleted);

        await holder.DisposeAsync();
        await waiterTask;
        Assert.Single(order);
        Assert.Equal(2, order[0]);
    }

    [Fact]
    public async Task AcquireAsync_DifferentCookies_AreConcurrent()
    {
        var gate = CreateGate();
        var order = new System.Collections.Concurrent.ConcurrentBag<int>();

        var t1 = Task.Run(async () =>
        {
            await using var _ = await gate.AcquireAsync("cookie1");
            order.Add(1);
            await Task.Delay(50);
        });

        var t2 = Task.Run(async () =>
        {
            await using var _ = await gate.AcquireAsync("cookie2");
            order.Add(2);
            await Task.Delay(50);
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(2, order.Count);
    }

    [Fact]
    public async Task AcquireAsync_DisposeReleasesLock()
    {
        var gate = CreateGate();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using (await gate.AcquireAsync("cookie1"))
        {
        }

        sw.Restart();
        await using (await gate.AcquireAsync("cookie1"))
        {
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"AcquireAsync after Dispose took {sw.ElapsedMilliseconds}ms — lock was not released");
    }

    [Fact]
    public async Task AcquireAsync_MultipleSequentialAcquiresWork()
    {
        var gate = CreateGate();

        for (int i = 0; i < 5; i++)
        {
            await using var handle = await gate.AcquireAsync("cookie1");
            Assert.NotNull(handle);
        }
    }

    [Fact]
    public async Task AcquireAsync_SerializationGuarantee()
    {
        var gate = CreateGate();
        var concurrentCount = 0;
        var maxConcurrent = 0;

        var holder = await gate.AcquireAsync("cookie1");

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var _ = await gate.AcquireAsync("cookie1");
            var c = Interlocked.Increment(ref concurrentCount);
            var prev = Interlocked.CompareExchange(ref maxConcurrent, c, c);
            if (c > prev) Interlocked.CompareExchange(ref maxConcurrent, c, prev);
            await Task.Delay(10);
            Interlocked.Decrement(ref concurrentCount);
        })).ToArray();

        await Task.Delay(50);
        await holder.DisposeAsync();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxConcurrent);
    }
}
