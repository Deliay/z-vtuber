using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Live;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RedisLiveEventBusTests(RedisFixture redis)
{
    private static LiveEvent Event(long roomId) => new(roomId, "danmaku", null, DateTime.UtcNow, "{\"cmd\":\"DANMU_MSG\"}");

    private static RedisChannel Channel(long roomId) =>
        new(LiveEventChannels.ChannelName(roomId), RedisChannel.PatternMode.Literal);

    private static async Task PublishAsync(RedisFixture redis, LiveEvent evt)
        => await redis.Connection.GetSubscriber().PublishAsync(Channel(evt.RoomId), JsonSerializer.Serialize(evt));

    // Redis 订阅在后台异步建立，等待订阅生效后再发布，避免 fire-and-forget 竞态。
    private static async Task WaitForSubscriptionAsync()
        => await Task.Delay(300);

    [Fact]
    public async Task Subscribe_FirstSubscriber_SubscribesToRedisAndDelivers()
    {
        var bus = new LiveEventBus(redis.Connection);
        var sub = bus.Subscribe(123456);
        await WaitForSubscriptionAsync();

        await PublishAsync(redis, Event(123456));

        var received = await sub.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(123456L, received.RoomId);

        bus.Unsubscribe(123456, sub.Id);
    }

    [Fact]
    public async Task TwoSubscribers_BothReceiveBroadcast()
    {
        var bus = new LiveEventBus(redis.Connection);
        var s1 = bus.Subscribe(7);
        var s2 = bus.Subscribe(7);
        await WaitForSubscriptionAsync();

        await PublishAsync(redis, Event(7));

        var r1 = await s1.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var r2 = await s2.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7L, r1.RoomId);
        Assert.Equal(7L, r2.RoomId);

        bus.Unsubscribe(7, s1.Id);
        bus.Unsubscribe(7, s2.Id);
    }

    [Fact]
    public async Task LastUnsubscribe_StopsRedisDelivery()
    {
        var bus = new LiveEventBus(redis.Connection);
        var sub = bus.Subscribe(55);
        await WaitForSubscriptionAsync();

        // confirm active delivery first
        await PublishAsync(redis, Event(55));
        var received = await sub.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(55L, received.RoomId);

        bus.Unsubscribe(55, sub.Id);

        // last subscriber gone -> Redis subscription torn down, channel completed
        await sub.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await PublishAsync(redis, Event(55));
    }

    [Fact]
    public async Task Publish_WithoutSubscriber_ReturnsFalse()
    {
        var bus = new LiveEventBus(redis.Connection);

        Assert.False(bus.TryPublish(1, Event(1)));
    }

    [Fact]
    public async Task Subscribe_ThenTryPublishInProcess_Delivers()
    {
        var bus = new LiveEventBus(redis.Connection);
        var sub = bus.Subscribe(2);

        Assert.True(bus.TryPublish(2, Event(2)));
        var received = await sub.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2L, received.RoomId);

        bus.Unsubscribe(2, sub.Id);
    }
}