using Z.Vtuber.Crawler.Jobs;

namespace Z.Vtuber.Tests;

public class CrawlLockProviderTests
{
    [Fact]
    public async Task SameKey_SerializesAccess()
    {
        var provider = new CrawlLockProvider();
        var gate = provider.GetLock("mid:1");

        var running = 0;
        var maxConcurrent = 0;

        async Task Work()
        {
            await gate.WaitAsync();
            try
            {
                var current = Interlocked.Increment(ref running);
                Interlocked.Exchange(ref maxConcurrent, Math.Max(maxConcurrent, current));
                await Task.Delay(50);
                Interlocked.Decrement(ref running);
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Work(), Work(), Work());

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task DifferentKeys_DoNotBlockEachOther()
    {
        var provider = new CrawlLockProvider();
        var gateA = provider.GetLock("mid:1");
        var gateB = provider.GetLock("mid:2");

        await gateA.WaitAsync();
        try
        {
            var acquireB = gateB.WaitAsync();
            var completed = await Task.WhenAny(acquireB, Task.Delay(200));

            Assert.Same(acquireB, completed);
        }
        finally
        {
            gateA.Release();
            gateB.Release();
        }
    }

    [Fact]
    public void GetLock_ReturnsSameInstanceForSameKey()
    {
        var provider = new CrawlLockProvider();
        Assert.Same(provider.GetLock("mid:1"), provider.GetLock("mid:1"));
        Assert.NotSame(provider.GetLock("mid:1"), provider.GetLock("mid:2"));
    }
}
