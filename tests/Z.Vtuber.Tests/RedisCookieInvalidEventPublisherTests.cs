using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RedisCookieInvalidEventPublisherTests(RedisFixture redis)
{
    private static readonly RedisChannel Channel = new(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);

    [Fact]
    public async Task PublishAsync_SendsEventToChannel_SubscriberReceivesSamePayload()
    {
        var repo = new MockCookieAccountInfoRepository();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, repo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
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
    public async Task PublishAsync_WithAccountInfo_IncludesMidAndUname()
    {
        var cookieId = ObjectId.GenerateNewId();
        var repo = new MockCookieAccountInfoRepository();
        repo.Upserts.Add(new CookieAccountInfoRecord
        {
            CookieId = cookieId,
            Mid = 12345,
            Uname = "TestUser",
        });

        // Mock GetByCookieIdsAsync 返回记录
        var mockRepo = new MockCookieAccountInfoRepositoryForPublish(cookieId);
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, mockRepo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync(cookieId.ToString(), "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)msg.Message!);
        Assert.Equal(cookieId.ToString(), evt!.CookieId);
        Assert.Equal(12345, evt.Mid);
        Assert.Equal("TestUser", evt.Uname);

        await subscriber.UnsubscribeAsync(Channel);
    }

    [Fact]
    public async Task PublishAsync_AccountInfoMissing_MidAndUnameNull()
    {
        var cookieId = ObjectId.GenerateNewId();
        var repo = new MockCookieAccountInfoRepositoryForPublish(ObjectId.Empty); // 返回空
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, repo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
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
        var repo = new MockCookieAccountInfoRepository();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, repo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);

        await publisher.PublishAsync(ObjectId.GenerateNewId().ToString(), "test_reason");

        Assert.True(true);
    }

    [Fact]
    public async Task PublishAsync_PayloadDoesNotContainCookieString()
    {
        var cookieId = ObjectId.GenerateNewId().ToString();
        var repo = new MockCookieAccountInfoRepository();
        var publisher = new RedisCookieInvalidEventPublisher(redis.Connection, repo, NullLogger<RedisCookieInvalidEventPublisher>.Instance);
        var subscriber = redis.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(Channel);

        await publisher.PublishAsync(cookieId, "test_reason");

        var msg = await queue.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var rawJson = (string)msg.Message!;
        Assert.DoesNotContain("cookie_string", rawJson);

        await subscriber.UnsubscribeAsync(Channel);
    }

    private sealed class MockCookieAccountInfoRepositoryForPublish : ICookieAccountInfoRepository
    {
        private readonly ObjectId _cookieIdToReturn;

        public MockCookieAccountInfoRepositoryForPublish(ObjectId cookieIdToReturn)
        {
            _cookieIdToReturn = cookieIdToReturn;
        }

        public Task UpsertAsync(CookieAccountInfoRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<Dictionary<ObjectId, CookieAccountInfoRecord>> GetByCookieIdsAsync(
            IReadOnlyCollection<ObjectId> cookieIds, CancellationToken ct = default)
        {
            if (_cookieIdToReturn == ObjectId.Empty)
                return Task.FromResult(new Dictionary<ObjectId, CookieAccountInfoRecord>());

            return Task.FromResult(new Dictionary<ObjectId, CookieAccountInfoRecord>
            {
                [_cookieIdToReturn] = new CookieAccountInfoRecord
                {
                    CookieId = _cookieIdToReturn,
                    Mid = 12345,
                    Uname = "TestUser",
                }
            });
        }
    }
}
