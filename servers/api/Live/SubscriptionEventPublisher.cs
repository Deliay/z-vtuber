using System.Text.Json;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Live;

/// <summary>
/// 将 api 进程内 <see cref="ISubscriptionService.Subscribed"/> 事件跨进程广播到 Redis，
/// 供 crawler 侧即时抓取新订阅主播资料（api 与 crawler 为独立进程，进程内事件不可达）。
/// 以 HostedService 注册：宿主启动即实例化并挂载事件监听，避免单例惰性实例化导致订阅者缺失。
/// </summary>
public sealed class SubscriptionEventPublisher(
    ISubscriptionService subscription,
    IConnectionMultiplexer redis,
    ILogger<SubscriptionEventPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        subscription.Subscribed += OnSubscribed;
        logger.LogInformation("Subscription event publisher attached.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停机
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        subscription.Subscribed -= OnSubscribed;
        return base.StopAsync(cancellationToken);
    }

    private void OnSubscribed(long mid, SubscriptionType type) => _ = PublishAsync(mid, type);

    private async Task PublishAsync(long mid, SubscriptionType type)
    {
        try
        {
            var channel = new RedisChannel(SubscriptionEventChannels.Subscribed, RedisChannel.PatternMode.Literal);
            await redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(new SubscriptionEvent(mid, type)));
        }
        catch (Exception ex)
        {
            // 发布失败仅记日志，订阅本身已落库；crawler 周期任务兜底
            logger.LogWarning(ex, "Failed to publish subscription event for mid {Mid} type {Type}.", mid, type);
        }
    }
}

public static class SubscriptionEventsServiceCollectionExtensions
{
    /// <summary>
    /// 注册订阅事件跨进程发布器（HostedService）。api Program 与测试复用同一装配方式。
    /// </summary>
    public static IServiceCollection AddSubscriptionEvents(this IServiceCollection services) =>
        services.AddHostedService<SubscriptionEventPublisher>();
}
