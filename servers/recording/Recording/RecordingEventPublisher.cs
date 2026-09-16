using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;

namespace Z.Vtuber.Recording.Recording;

/// <summary>录播开始/结束事件发布契约（Redis pub/sub，fire-and-forget）。</summary>
public interface IRecordingEventPublisher
{
    Task PublishStartedAsync(RecordingStartedEvent recordingEvent, CancellationToken ct = default);

    Task PublishFinishedAsync(RecordingFinishedEvent recordingEvent, CancellationToken ct = default);
}

/// <summary>
/// Redis 录播事件发布：序列化事件载荷发布到 <see cref="RecordingEventChannels.Started"/> /
/// <see cref="RecordingEventChannels.Finished"/>；发布失败仅记日志，不向上抛出。
/// </summary>
public sealed class RedisRecordingEventPublisher(
    IConnectionMultiplexer redis,
    ILogger<RedisRecordingEventPublisher> logger) : IRecordingEventPublisher
{
    public Task PublishStartedAsync(RecordingStartedEvent recordingEvent, CancellationToken ct = default) =>
        PublishAsync(RecordingEventChannels.Started, recordingEvent, recordingEvent.RoomId);

    public Task PublishFinishedAsync(RecordingFinishedEvent recordingEvent, CancellationToken ct = default) =>
        PublishAsync(RecordingEventChannels.Finished, recordingEvent, recordingEvent.RoomId);

    private async Task PublishAsync<T>(string channel, T payload, long roomId)
    {
        try
        {
            var redisChannel = new RedisChannel(channel, RedisChannel.PatternMode.Literal);
            var json = JsonSerializer.Serialize(payload);
            await redis.GetSubscriber().PublishAsync(redisChannel, json);
            logger.LogDebug("Published {Channel} event for room {RoomId}.", channel, roomId);
        }
        catch (Exception ex)
        {
            // 发布失败不影响录制与落库，由 transcript-consumer 对账兜底
            logger.LogWarning(ex, "Failed to publish {Channel} event for room {RoomId}; continuing.", channel, roomId);
        }
    }
}
