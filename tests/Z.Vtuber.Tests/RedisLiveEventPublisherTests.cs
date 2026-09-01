using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Crawler.Live;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RedisLiveEventPublisherTests(RedisFixture redis)
{
    private static LiveEvent Event(long roomId) => new(roomId, "danmaku", null, DateTime.UtcNow, "{\"cmd\":\"DANMU_MSG\"}");

    private static RedisChannel Channel(long roomId) =>
        new(LiveEventChannels.ChannelName(roomId), RedisChannel.PatternMode.Literal);

    [Fact]
    public async Task PublishAsync_SendsEventToChannel_SubscriberReceivesSamePayload()
    {
        var publisher = new RedisLiveEventPublisher(redis.Connection, NullLogger<RedisLiveEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel(42));

        await publisher.PublishAsync(Event(42));

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<LiveEvent>((string)msg.Message!);
        Assert.Equal(42L, evt!.RoomId);
        Assert.Equal("danmaku", evt.Type);

        await subscriber.UnsubscribeAsync(Channel(42));
    }

    [Fact]
    public async Task PublishAsync_NoSubscriber_DoesNotThrow()
    {
        var publisher = new RedisLiveEventPublisher(redis.Connection, NullLogger<RedisLiveEventPublisher>.Instance);

        await publisher.PublishAsync(Event(99));

        Assert.True(true);
    }
}