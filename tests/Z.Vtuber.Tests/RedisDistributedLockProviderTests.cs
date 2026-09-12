using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Locking;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RedisDistributedLockProviderTests(RedisFixture redis)
{
    private RedisDistributedLockProvider CreateProvider()
    {
        var options = Options.Create(new CookieServiceOptions());
        return new RedisDistributedLockProvider(redis.Connection, options, NullLogger<RedisDistributedLockProvider>.Instance);
    }

    [Fact]
    public async Task AcquireAndRelease_OtherCanAcquire()
    {
        var provider1 = CreateProvider();
        var provider2 = CreateProvider();

        var handle1 = await provider1.AcquireAsync("test-lock", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.NotNull(handle1);

        await handle1.DisposeAsync();

        var handle2 = await provider2.AcquireAsync("test-lock", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.NotNull(handle2);
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_WhileHeld_ReturnsNullWithinTimeout()
    {
        var provider1 = CreateProvider();
        var provider2 = CreateProvider();

        var handle1 = await provider1.AcquireAsync("test-lock", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.NotNull(handle1);

        var handle2 = await provider2.AcquireAsync("test-lock", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        Assert.Null(handle2);

        await handle1.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_TtlExpiry_ReleasesAutomatically()
    {
        var provider = CreateProvider();

        var handle = await provider.AcquireAsync("ttl-lock", TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));
        Assert.NotNull(handle);

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var handle2 = await provider.AcquireAsync("ttl-lock", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        Assert.NotNull(handle2);
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var provider = CreateProvider();

        var handle = await provider.AcquireAsync("idempotent-lock", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.NotNull(handle);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        var handle2 = await provider.AcquireAsync("idempotent-lock", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        Assert.NotNull(handle2);
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task DifferentKeys_AreIndependent()
    {
        var provider = CreateProvider();

        var handle1 = await provider.AcquireAsync("lock-a", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        var handle2 = await provider.AcquireAsync("lock-b", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        Assert.NotNull(handle1);
        Assert.NotNull(handle2);

        await handle1.DisposeAsync();
        await handle2.DisposeAsync();
    }
}
