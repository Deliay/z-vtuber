using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RedisCookieInvalidEventPublisherTests(RedisFixture redis)
{
    private static readonly RedisChannel Channel = new(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);

    [Fact]
    public async Task PublishAsync_SendsEventToChannel_SubscriberReceivesSamePayload()
    {
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        var cookieId = ObjectId.GenerateNewId().ToString();
        await publisher.PublishAsync(cookieId, "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)msg.Message!);
        Assert.Equal(cookieId, evt!.CookieId);
        Assert.Equal("test_reason", evt.Reason);
        Assert.Null(evt.Mid);
        Assert.Null(evt.Uname);

        await subscriber.UnsubscribeAsync(Channel);
    }

    [Fact]
    public async Task PublishAsync_AccountInfoMissing_MidAndUnameNull()
    {
        var cookieId = ObjectId.GenerateNewId();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync(cookieId.ToString(), "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)msg.Message!);
        Assert.Equal(cookieId.ToString(), evt!.CookieId);
        Assert.Null(evt.Mid);
        Assert.Null(evt.Uname);

        await subscriber.UnsubscribeAsync(Channel);
    }

    [Fact]
    public async Task PublishAsync_NoSubscriber_DoesNotThrow()
    {
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, NullLogger<RedisCookieInvalidEventPublisher>.Instance);

        await publisher.PublishAsync(ObjectId.GenerateNewId().ToString(), "test_reason");

        Assert.True(true);
    }

    [Fact]
    public async Task PublishAsync_PayloadDoesNotContainCookieString()
    {
        var cookieId = ObjectId.GenerateNewId().ToString();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync(cookieId, "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var rawJson = (string)msg.Message!;
        Assert.DoesNotContain("cookie_string", rawJson);

        await subscriber.UnsubscribeAsync(Channel);
    }
}
