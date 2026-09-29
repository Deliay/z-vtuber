using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Emotion;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class EmotionTaskLockTests(RedisFixture fixture)
{
    private static RedisEmotionTaskLockProvider CreateProvider(
        IConnectionMultiplexer redis, int ttlSeconds = 2) =>
        new(redis,
            new EmotionTaskOptions { LockTtlSeconds = ttlSeconds, LockRenewIntervalSeconds = 1 },
            NullLogger<RedisEmotionTaskLockProvider>.Instance);

    [Fact]
    public async Task AcquireAndRelease_AllowsOthersToAcquire()
    {
        var provider = CreateProvider(fixture.Connection);
        var taskId = ObjectId.GenerateNewId();

        var first = await provider.TryAcquireAsync(taskId);
        Assert.NotNull(first);
        await first!.ReleaseAsync();
        await first.DisposeAsync();

        var second = await provider.TryAcquireAsync(taskId);
        Assert.NotNull(second);
        await second!.ReleaseAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task HeldLock_RejectsSecondHolder()
    {
        var provider = CreateProvider(fixture.Connection);
        var taskId = ObjectId.GenerateNewId();

        await using var first = await provider.TryAcquireAsync(taskId);
        var second = await provider.TryAcquireAsync(taskId);

        Assert.Null(second);
    }

    [Fact]
    public async Task Renew_KeepsLockBeyondTtl()
    {
        var provider = CreateProvider(fixture.Connection, ttlSeconds: 1);
        var taskId = ObjectId.GenerateNewId();

        await using var handle = await provider.TryAcquireAsync(taskId);

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
        var taskId = ObjectId.GenerateNewId();

        await using var handleA = await providerA.TryAcquireAsync(taskId);
        Assert.NotNull(handleA);

        await Task.Delay(1500);

        var handleB = await providerB.TryAcquireAsync(taskId);
        Assert.NotNull(handleB);
        await handleB!.ReleaseAsync();
        await handleB.DisposeAsync();
    }

    [Fact]
    public async Task Fencing_DetectsLostOwnership()
    {
        var provider = CreateProvider(fixture.Connection, ttlSeconds: 1);
        var taskId = ObjectId.GenerateNewId();

        await using var handle = await provider.TryAcquireAsync(taskId);
        Assert.True(await handle!.CheckOwnershipAsync());

        var db = fixture.Connection.GetDatabase();
        await db.StringSetAsync(RedisEmotionTaskLockProvider.KeyFor(taskId), "another-token");

        Assert.False(await handle.CheckOwnershipAsync());
        Assert.True(handle.Lost);
    }

    [Fact]
    public async Task Release_OnlyDeletesOwnToken()
    {
        var provider = CreateProvider(fixture.Connection);
        var taskId = ObjectId.GenerateNewId();

        var handle = await provider.TryAcquireAsync(taskId);
        var db = fixture.Connection.GetDatabase();
        await db.StringSetAsync(RedisEmotionTaskLockProvider.KeyFor(taskId), "another-token");

        await handle!.ReleaseAsync();

        var current = await db.StringGetAsync(RedisEmotionTaskLockProvider.KeyFor(taskId));
        Assert.Equal("another-token", current.ToString());
    }

    [Fact]
    public async Task CrossInstance_MutualExclusion()
    {
        var providerA = CreateProvider(fixture.Connection);
        var providerB = CreateProvider(fixture.Connection);
        var taskId = ObjectId.GenerateNewId();

        await using var handleA = await providerA.TryAcquireAsync(taskId);

        var handleB = await providerB.TryAcquireAsync(taskId);
        Assert.Null(handleB);
    }

    [Fact]
    public async Task IsHeld_ReflectsOwnershipState()
    {
        var provider = CreateProvider(fixture.Connection);
        var taskId = ObjectId.GenerateNewId();

        Assert.False(await provider.IsHeldAsync(taskId));

        var handle = await provider.TryAcquireAsync(taskId);
        Assert.True(await provider.IsHeldAsync(taskId));

        await handle!.ReleaseAsync();
        Assert.False(await provider.IsHeldAsync(taskId));
    }

    [Fact]
    public async Task RedisUnavailable_OwnershipCheckTreatedAsLost_AndIsHeldAssumesHeld()
    {
        // 指向不可达端口：Redis 抖动不得把在跑任务打成 FAILED，而应按失锁处理；IsHeld 保守按持有避免双跑
        var config = ConfigurationOptions.Parse("127.0.0.1:1");
        config.ConnectTimeout = 200;
        config.ConnectRetry = 1;
        config.AbortOnConnectFail = false;
        using var dead = await ConnectionMultiplexer.ConnectAsync(config);
        var taskId = ObjectId.GenerateNewId();

        var provider = CreateProvider(dead);
        Assert.True(await provider.IsHeldAsync(taskId));

        var handle = new RedisEmotionTaskLockProvider.RedisEmotionTaskLock(
            dead.GetDatabase(),
            RedisEmotionTaskLockProvider.KeyFor(taskId),
            taskId,
            "token",
            new EmotionTaskOptions(),
            NullLogger<RedisEmotionTaskLockProvider>.Instance);

        Assert.False(await handle.CheckOwnershipAsync());
        Assert.True(handle.Lost);
    }

    [Fact]
    public async Task RedisUnavailable_RenewTreatsAsLost_WithoutThrowing()
    {
        // Redis 抖动时 RenewAsync 必须自行兜底（置 Lost、返回 false），不得把异常抛进续期循环导致循环终止
        var config = ConfigurationOptions.Parse("127.0.0.1:1");
        config.ConnectTimeout = 200;
        config.ConnectRetry = 1;
        config.AbortOnConnectFail = false;
        using var dead = await ConnectionMultiplexer.ConnectAsync(config);
        var taskId = ObjectId.GenerateNewId();

        var handle = new RedisEmotionTaskLockProvider.RedisEmotionTaskLock(
            dead.GetDatabase(),
            RedisEmotionTaskLockProvider.KeyFor(taskId),
            taskId,
            "token",
            new EmotionTaskOptions(),
            NullLogger<RedisEmotionTaskLockProvider>.Instance);

        var renewed = await handle.RenewAsync();

        Assert.False(renewed);
        Assert.True(handle.Lost);
    }
}
