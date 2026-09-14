using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RecordingLockTests(RedisFixture fixture)
{
    private static RedisRecordingLockProvider CreateProvider(IConnectionMultiplexer redis, int ttlSeconds = 2) =>
        new(redis,
            new RecordingOptions { LockTtlSeconds = ttlSeconds, LockRenewIntervalSeconds = 1 },
            NullLogger<RedisRecordingLockProvider>.Instance);

    [Fact]
    public async Task AcquireAndRelease_AllowsOthersToAcquire()
    {
        var provider = CreateProvider(fixture.Connection);
        var roomId = 900001L;

        var first = await provider.TryAcquireAsync(roomId);
        Assert.NotNull(first);
        await first!.ReleaseAsync();
        await first.DisposeAsync();

        var second = await provider.TryAcquireAsync(roomId);
        Assert.NotNull(second);
        await second!.ReleaseAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task HeldLock_RejectsSecondHolder()
    {
        var provider = CreateProvider(fixture.Connection);
        var roomId = 900002L;

        await using var first = await provider.TryAcquireAsync(roomId);
        var second = await provider.TryAcquireAsync(roomId);

        Assert.Null(second);
    }

    [Fact]
    public async Task Renew_KeepsLockBeyondTtl()
    {
        var provider = CreateProvider(fixture.Connection, ttlSeconds: 1);
        var roomId = 900003L;

        await using var handle = await provider.TryAcquireAsync(roomId);

        await Task.Delay(700);
        Assert.True(await handle!.RenewAsync());

        await Task.Delay(700);
        Assert.True(await handle.CheckOwnershipAsync());
    }

    [Fact]
    public async Task ExpiredLock_IsTakenOverByAnotherInstance()
    {
        var providerA = CreateProvider(fixture.Connection, ttlSeconds: 1);
        var providerB = CreateProvider(fixture.Connection, ttlSeconds: 1);
        var roomId = 900004L;

        await using var handleA = await providerA.TryAcquireAsync(roomId);
        Assert.NotNull(handleA);

        await Task.Delay(1500);

        var handleB = await providerB.TryAcquireAsync(roomId);
        Assert.NotNull(handleB);
        await handleB!.ReleaseAsync();
        await handleB.DisposeAsync();
    }

    [Fact]
    public async Task Fencing_DetectsLostOwnership()
    {
        var provider = CreateProvider(fixture.Connection, ttlSeconds: 1);
        var roomId = 900005L;

        await using var handle = await provider.TryAcquireAsync(roomId);
        Assert.True(await handle!.CheckOwnershipAsync());

        // 锁被他人接管（模拟 token 变更）
        var db = fixture.Connection.GetDatabase();
        await db.StringSetAsync(RedisRecordingLockProvider.KeyFor(roomId), "another-token");

        Assert.False(await handle.CheckOwnershipAsync());
        Assert.True(handle.Lost);
    }

    [Fact]
    public async Task Release_OnlyDeletesOwnToken()
    {
        var provider = CreateProvider(fixture.Connection);
        var roomId = 900006L;

        var handle = await provider.TryAcquireAsync(roomId);
        var db = fixture.Connection.GetDatabase();
        await db.StringSetAsync(RedisRecordingLockProvider.KeyFor(roomId), "another-token");

        await handle!.ReleaseAsync();

        var current = await db.StringGetAsync(RedisRecordingLockProvider.KeyFor(roomId));
        Assert.Equal("another-token", current.ToString());
    }

    [Fact]
    public async Task CrossInstance_MutualExclusion()
    {
        var providerA = CreateProvider(fixture.Connection);
        var providerB = CreateProvider(fixture.Connection);
        var roomId = 900007L;

        await using var handleA = await providerA.TryAcquireAsync(roomId);

        var handleB = await providerB.TryAcquireAsync(roomId);
        Assert.Null(handleB);
    }
}
