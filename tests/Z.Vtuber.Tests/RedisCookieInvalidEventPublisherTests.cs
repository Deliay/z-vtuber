using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RedisCookieInvalidEventPublisherTests(RedisFixture redis)
{
    private static readonly RedisChannel Channel = new(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);

    [Fact]
    public async Task PublishAsync_SendsEventToChannel_SubscriberReceivesSamePayload()
    {
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, new MockCookieAccountInfoRepository(), NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        var cookieId = ObjectId.GenerateNewId().ToString();
        await publisher.PublishAsync(cookieId, "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)msg.Message!);
        Assert.Equal(cookieId, evt!.CookieId);
        Assert.Equal("test_reason", evt.Reason);
        Assert.Equal("bilibili", evt.Platform);

        await subscriber.UnsubscribeAsync(Channel);
    }

    [Fact]
    public async Task PublishAsync_WeiboPlatform_UsesSessionIdentityAndPlatform()
    {
        var sessionRepo = new FakeCookieSessionRepository();
        sessionRepo.Sessions.Add(new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            CookieId = "weibo-cookie-x",
            Mid = 998877,
            Uname = "微博用户",
        });
        var publisher = new RedisCookieInvalidEventPublisher(
            redis.Connection, "weibo", accountInfoRepository: null, sessionRepo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync("weibo-cookie-x", "session_invalid");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)msg.Message!);
        Assert.Equal("weibo-cookie-x", evt!.CookieId);
        Assert.Equal("weibo", evt.Platform);
        Assert.Equal(998877L, evt.Mid);
        Assert.Equal("微博用户", evt.Uname);

        await subscriber.UnsubscribeAsync(Channel);
    }

    [Fact]
    public async Task PublishAsync_AccountInfoPresent_IncludesMidAndUname()
    {
        var cookieId = ObjectId.GenerateNewId();
        var accountInfo = new CookieAccountInfoRecord
        {
            CookieId = cookieId,
            Mid = 12345,
            Uname = "test_user",
            FetchedAt = DateTime.UtcNow,
        };
        var mockRepo = new MockCookieAccountInfoRepository
        {
            LookupResults = new Dictionary<ObjectId, CookieAccountInfoRecord> { [cookieId] = accountInfo },
        };
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, mockRepo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync(cookieId.ToString(), "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)msg.Message!);
        Assert.Equal(cookieId.ToString(), evt!.CookieId);
        Assert.Equal(12345L, evt.Mid);
        Assert.Equal("test_user", evt.Uname);

        await subscriber.UnsubscribeAsync(Channel);
    }

    [Fact]
    public async Task PublishAsync_AccountInfoMissing_MidAndUnameNull()
    {
        var cookieId = ObjectId.GenerateNewId();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, new MockCookieAccountInfoRepository(), NullLogger<RedisCookieInvalidEventPublisher>.Instance);
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
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, new MockCookieAccountInfoRepository(), NullLogger<RedisCookieInvalidEventPublisher>.Instance);

        await publisher.PublishAsync(ObjectId.GenerateNewId().ToString(), "test_reason");

        Assert.True(true);
    }

    [Fact]
    public async Task PublishAsync_PayloadDoesNotContainCookieString()
    {
        var cookieId = ObjectId.GenerateNewId().ToString();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, new MockCookieAccountInfoRepository(), NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync(cookieId, "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var rawJson = (string)msg.Message!;
        Assert.DoesNotContain("cookie_string", rawJson);

        await subscriber.UnsubscribeAsync(Channel);
    }
}
