using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Jobs;

/// <summary>
/// 订阅 Redis 订阅事件频道，将远端（api 进程）创建的订阅桥接为本进程内的
/// <see cref="ISubscriptionService.Subscribed"/> 事件，触发 <see cref="AccountInfoJob"/> 等即时抓取。
/// </summary>
public sealed class SubscriptionEventSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriptionService _subscription;
    private readonly ILogger<SubscriptionEventSubscriber> _logger;

    public SubscriptionEventSubscriber(
        IConnectionMultiplexer redis,
        ISubscriptionService subscription,
        ILogger<SubscriptionEventSubscriber> logger)
    {
        _redis = redis;
        _subscription = subscription;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        var channel = new RedisChannel(SubscriptionEventChannels.Subscribed, RedisChannel.PatternMode.Literal);

        await subscriber.SubscribeAsync(channel, (_, value) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SubscriptionEvent>(value.ToString());
                if (evt is not null)
                {
                    _subscription.RaiseSubscribed(evt.Mid, evt.Type);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle cross-process subscription event.");
            }
        });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停机
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }
}
