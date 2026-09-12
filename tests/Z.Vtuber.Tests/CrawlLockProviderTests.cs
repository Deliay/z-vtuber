using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

public class CrawlLockProviderTests
{
    [Fact]
    public async Task AcquireAsync_ReturnsNonNullHandle()
    {
        var provider = new CrawlLockProvider(new FakeDistributedLockProvider());
        await using var handle = await provider.AcquireAsync("mid:1");
        Assert.NotNull(handle);
        Assert.Equal("crawl:mid:1", handle.Key);
    }

    [Fact]
    public async Task AcquireAsync_DifferentKeys_AreConcurrent()
    {
        var provider = new CrawlLockProvider(new FakeDistributedLockProvider());
        await using var handleA = await provider.AcquireAsync("mid:1");
        await using var handleB = await provider.AcquireAsync("mid:2");

        Assert.NotNull(handleA);
        Assert.NotNull(handleB);
    }
}
