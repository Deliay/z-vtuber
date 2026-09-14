using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Live;

/// <summary>
/// 将 api 进程内 <see cref="ISubscriptionService.Subscribed"/> 事件跨进程广播到 Redis，
/// 供 crawler 侧即时抓取新订阅主播资料（api 与 crawler 为独立进程，进程内事件不可达）。
/// </summary>
public sealed class SubscriptionEventPublisher : IDisposable
{
    private readonly ISubscriptionService _subscription;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SubscriptionEventPublisher> _logger;

    public SubscriptionEventPublisher(
        ISubscriptionService subscription,
        IConnectionMultiplexer redis,
        ILogger<SubscriptionEventPublisher> logger)
    {
        _subscription = subscription;
        _redis = redis;
        _logger = logger;
        _subscription.Subscribed += OnSubscribed;
    }

    private void OnSubscribed(long mid, SubscriptionType type) => _ = PublishAsync(mid, type);

    private async Task PublishAsync(long mid, SubscriptionType type)
    {
        try
        {
            var channel = new RedisChannel(SubscriptionEventChannels.Subscribed, RedisChannel.PatternMode.Literal);
            await _redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(new SubscriptionEvent(mid, type)));
        }
        catch (Exception ex)
        {
            // 发布失败仅记日志，订阅本身已落库；crawler 周期任务兜底
            _logger.LogWarning(ex, "Failed to publish subscription event for mid {Mid} type {Type}.", mid, type);
        }
    }

    public void Dispose() => _subscription.Subscribed -= OnSubscribed;
}
