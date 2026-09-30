using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Notifier.Dispatch;
using Z.Vtuber.Notifier.Events;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class CookieInvalidEventSubscriberTests(RedisFixture redis)
{
    private static readonly RedisChannel Channel = new(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);

    [Fact]
    public async Task ExecuteAsync_ReceivesEvent_DispatchesToService()
    {
        CookieInvalidEvent? receivedEvent = null;
        var dispatchService = new FakeNotificationDispatchService(evt =>
        {
            receivedEvent = evt;
            return Task.CompletedTask;
        });

        var subscriber = new CookieInvalidEventSubscriber(redis.Connection, dispatchService, NullLogger<CookieInvalidEventSubscriber>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = subscriber.StartAsync(cts.Token);

        // 等待订阅建立
        await WaitForSubscriptionAsync();

        var pubSubscriber = redis.Connection.GetSubscriber();
        var cookieId = ObjectId.GenerateNewId().ToString();
        var evt = new CookieInvalidEvent
        {
            CookieId = cookieId,
            Mid = 12345,
            Uname = "TestUser",
            OccurredAt = DateTime.UtcNow,
            Reason = "test_reason",
        };
        await pubSubscriber.PublishAsync(Channel, JsonSerializer.Serialize(evt));

        // 轮询等待消息处理完成
        await TestWait.UntilAsync(() => receivedEvent is not null, TimeSpan.FromSeconds(5));

        Assert.NotNull(receivedEvent);
        Assert.Equal(cookieId, receivedEvent!.CookieId);
        Assert.Equal(12345, receivedEvent.Mid);
        Assert.Equal("TestUser", receivedEvent.Uname);

        await subscriber.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidMessage_LogsWarningAndContinues()
    {
        var dispatchCount = 0;
        var dispatchService = new FakeNotificationDispatchService(evt =>
        {
            dispatchCount++;
            return Task.CompletedTask;
        });

        var subscriber = new CookieInvalidEventSubscriber(redis.Connection, dispatchService, NullLogger<CookieInvalidEventSubscriber>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = subscriber.StartAsync(cts.Token);

        await WaitForSubscriptionAsync();

        var pubSubscriber = redis.Connection.GetSubscriber();
        // 发送无效消息
        await pubSubscriber.PublishAsync(Channel, "invalid json");

        // 发送有效消息
        var cookieId = ObjectId.GenerateNewId().ToString();
        var evt = new CookieInvalidEvent
        {
            CookieId = cookieId,
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };
        await pubSubscriber.PublishAsync(Channel, JsonSerializer.Serialize(evt));

        await TestWait.UntilAsync(() => dispatchCount >= 1, TimeSpan.FromSeconds(5));

        Assert.Equal(1, dispatchCount);

        await subscriber.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromChannel()
    {
        var subscriber = new CookieInvalidEventSubscriber(redis.Connection, new FakeNotificationDispatchService(), NullLogger<CookieInvalidEventSubscriber>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = subscriber.StartAsync(cts.Token);

        await WaitForSubscriptionAsync();

        await subscriber.StopAsync(CancellationToken.None);

        // 验证停止后不再接收消息：轮询到 Redis 频道订阅计数归零
        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints().First());
        await TestWait.UntilAsync(
            () => server.SubscriptionSubscriberCount(Channel) == 0,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25));

        var pubSubscriber = redis.Connection.GetSubscriber();
        var cookieId = ObjectId.GenerateNewId().ToString();
        var evt = new CookieInvalidEvent
        {
            CookieId = cookieId,
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };
        await pubSubscriber.PublishAsync(Channel, JsonSerializer.Serialize(evt));

        // 如果没有异常，说明已正确退订
        Assert.True(true);
    }

    private async Task WaitForSubscriptionAsync()
    {
        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints().First());
        await TestWait.UntilAsync(
            () => server.SubscriptionSubscriberCount(Channel) > 0,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25));
    }

    private sealed class FakeNotificationDispatchService : INotificationDispatchService
    {
        private readonly Func<CookieInvalidEvent, Task>? _onDispatch;

        public FakeNotificationDispatchService(Func<CookieInvalidEvent, Task>? onDispatch = null)
        {
            _onDispatch = onDispatch;
        }

        public Task DispatchAsync(CookieInvalidEvent evt, CancellationToken ct = default)
        {
            return _onDispatch?.Invoke(evt) ?? Task.CompletedTask;
        }
    }
}
