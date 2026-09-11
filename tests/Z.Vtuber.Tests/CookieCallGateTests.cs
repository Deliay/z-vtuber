using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public class CookieCallGateTests
{
    [Fact]
    public async Task AcquireAsync_ConcurrentCallsForSameCookie_AreSerialized()
    {
        var gate = new CookieCallGate();
        var order = new List<int>();
        var holder = await gate.AcquireAsync("cookie1");

        var waiterTask = Task.Run(async () =>
        {
            using var _ = await gate.AcquireAsync("cookie1");
            order.Add(2);
        });

        // Waiter should not complete while holder is active
        await Task.Delay(100);
        Assert.False(waiterTask.IsCompleted);

        // Release holder — waiter should now complete
        holder.Dispose();
        await waiterTask;
        Assert.Single(order);
        Assert.Equal(2, order[0]);
    }

    [Fact]
    public async Task AcquireAsync_DifferentCookies_AreConcurrent()
    {
        var gate = new CookieCallGate();
        var order = new System.Collections.Concurrent.ConcurrentBag<int>();

        var t1 = Task.Run(async () =>
        {
            using var _ = await gate.AcquireAsync("cookie1");
            order.Add(1);
            await Task.Delay(50);
        });

        var t2 = Task.Run(async () =>
        {
            using var _ = await gate.AcquireAsync("cookie2");
            order.Add(2);
            await Task.Delay(50);
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(2, order.Count);
    }

    [Fact]
    public async Task AcquireAsync_DisposeReleasesLock()
    {
        var gate = new CookieCallGate();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using (await gate.AcquireAsync("cookie1"))
        {
            // Lock held
        }

        // Lock should be immediately available
        sw.Restart();
        using (await gate.AcquireAsync("cookie1"))
        {
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"AcquireAsync after Dispose took {sw.ElapsedMilliseconds}ms — lock was not released");
    }

    [Fact]
    public async Task AcquireAsync_MultipleSequentialAcquiresWork()
    {
        var gate = new CookieCallGate();

        for (int i = 0; i < 5; i++)
        {
            using var handle = await gate.AcquireAsync("cookie1");
            Assert.NotNull(handle);
        }
    }

    [Fact]
    public async Task AcquireAsync_SerializationGuarantee()
    {
        // Verifies that N concurrent callers are serialized (not parallel)
        var gate = new CookieCallGate();
        var concurrentCount = 0;
        var maxConcurrent = 0;

        var holder = await gate.AcquireAsync("cookie1");

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var _ = await gate.AcquireAsync("cookie1");
            var c = Interlocked.Increment(ref concurrentCount);
            var prev = Interlocked.CompareExchange(ref maxConcurrent, c, c);
            if (c > prev) Interlocked.CompareExchange(ref maxConcurrent, c, prev);
            await Task.Delay(10);
            Interlocked.Decrement(ref concurrentCount);
        })).ToArray();

        await Task.Delay(50); // Let tasks queue up
        holder.Dispose();
        await Task.WhenAll(tasks);

        // With serialization, only 1 caller should be active at a time
        Assert.Equal(1, maxConcurrent);
    }
}
