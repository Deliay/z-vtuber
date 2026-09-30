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

    // Redis 订阅在后台异步建立，轮询到频道订阅计数可见后再发布，避免 fire-and-forget 竞态。
    private async Task WaitForSubscriptionAsync(long roomId)
    {
        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints().First());
        await TestWait.UntilAsync(
            () => SubscriberCount(server, Channel(roomId)) > 0,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task Subscribe_FirstSubscriber_SubscribesToRedisAndDelivers()
    {
        var bus = new LiveEventBus(redis.Connection);
        var sub = bus.Subscribe(123456);
        await WaitForSubscriptionAsync(123456);

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
        await WaitForSubscriptionAsync(7);

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
        await WaitForSubscriptionAsync(55);

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
    public async Task LastUnsubscribe_RemovesRedisSubscription()
    {
        var bus = new LiveEventBus(redis.Connection);
        var sub = bus.Subscribe(4242);
        await WaitForSubscriptionAsync(4242);

        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints().First());

        // 确认订阅已建立：Redis 上存在对该频道的订阅
        Assert.True(await WaitUntilAsync(
            () => SubscriberCount(server, Channel(4242)) > 0,
            TimeSpan.FromSeconds(5)),
            "Expected a Redis subscription to be active after Subscribe.");

        bus.Unsubscribe(4242, sub.Id);

        // 安静房间退订后 Redis 订阅真正拆除：订阅计数归零（而非仅停止投递）
        Assert.True(await WaitUntilAsync(
            () => SubscriberCount(server, Channel(4242)) == 0,
            TimeSpan.FromSeconds(5)),
            "Redis subscription should be removed after the last unsubscribe.");
    }

    private static long SubscriberCount(IServer server, RedisChannel channel)
        => server.SubscriptionSubscriberCount(channel);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        try
        {
            await TestWait.UntilAsync(condition, timeout, TimeSpan.FromMilliseconds(25));
            return true;
        }
        catch (Xunit.Sdk.TrueException)
        {
            return false;
        }
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

    [Fact]
    public async Task ResubscribeRace_OldPumpTeardown_DoesNotSilenceRoom()
    {
        // 回归：旧泵 finally/重建路径的退订必须是队列级（ChannelMessageQueue.UnsubscribeAsync），
        // 绝不能是频道级（subscriber.UnsubscribeAsync(channel)）——频道级退订会清除同频道全部 handler，
        // 在「旧泵 teardown 与新泵 SUBSCRIBE 交叠」时误拆新泵订阅，导致房间静默失聪。
        var bus = new LiveEventBus(redis.Connection);
        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints().First());
        const long room = 888001;

        LiveEventSubscription? active = null;
        for (var i = 0; i < 5; i++)
        {
            // 退订旧订阅（若存在）→ 触发旧泵 teardown
            if (active is not null)
            {
                bus.Unsubscribe(room, active.Id);
            }

            // 立即对同一频道重订阅 → 新 RoomState + 新泵，与旧泵 teardown 交叠
            active = bus.Subscribe(room);
            await WaitUntilAsync(() => SubscriberCount(server, Channel(room)) > 0, TimeSpan.FromSeconds(2));

            // 新订阅者必须仍能收到事件（旧泵 teardown 不得误拆新泵订阅）
            await PublishAsync(redis, Event(room));
            var received = await active.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(room, received.RoomId);
        }

        bus.Unsubscribe(room, active.Id);
        await WaitUntilAsync(() => SubscriberCount(server, Channel(room)) == 0, TimeSpan.FromSeconds(5));
    }
}
