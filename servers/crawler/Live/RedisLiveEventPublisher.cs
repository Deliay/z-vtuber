using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;

namespace Z.Vtuber.Crawler.Live;

public interface ILiveEventPublisher
{
    Task PublishAsync(LiveEvent liveEvent, CancellationToken ct = default);
}

public sealed class RedisLiveEventPublisher(
    IConnectionMultiplexer redis,
    ILogger<RedisLiveEventPublisher> logger) : ILiveEventPublisher
{
    public async Task PublishAsync(LiveEvent liveEvent, CancellationToken ct = default)
    {
        try
        {
            var channel = new RedisChannel(LiveEventChannels.ChannelName(liveEvent.RoomId), RedisChannel.PatternMode.Literal);
            await redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(liveEvent));
        }
        catch (Exception ex)
        {
            // 发布失败仅记日志，不影响落库与长连接
            logger.LogWarning(ex, "Failed to publish live event for room {RoomId}; continuing.", liveEvent.RoomId);
        }
    }
}
