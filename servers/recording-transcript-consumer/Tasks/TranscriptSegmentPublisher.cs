using System.Text.Json;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

public interface ITranscriptSegmentPublisher
{
    Task PublishAsync(ObjectId taskId, StreamingAsrSegment segment, CancellationToken ct = default);
}

public sealed class RedisTranscriptSegmentPublisher(
    IConnectionMultiplexer redis,
    ILogger<RedisTranscriptSegmentPublisher> logger) : ITranscriptSegmentPublisher
{
    public async Task PublishAsync(ObjectId taskId, StreamingAsrSegment segment, CancellationToken ct = default)
    {
        var evt = new TranscriptSegmentEvent(taskId.ToString(), segment.Start, segment.End, segment.Text);
        var channel = new RedisChannel(
            RecordingEventChannels.SegmentChannel(taskId.ToString()), RedisChannel.PatternMode.Literal);
        try
        {
            await redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(evt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish transcript segment for task {TaskId}", taskId);
        }
    }
}
