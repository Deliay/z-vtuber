using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;

namespace Z.Vtuber.RecordingTranscript.Consumer.Events;

/// <summary>订阅 recording-started，roomId 启用自动识别时按设置绑定的 ModelId 建 mode=live 任务。</summary>
public sealed class RecordingStartedSubscriber(
    IConnectionMultiplexer redis,
    TranscriptEnqueueService enqueue,
    ILogger<RecordingStartedSubscriber> logger) : BackgroundService
{
    private ISubscriber? _subscriber;
    private ChannelMessageQueue? _queue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriber = redis.GetSubscriber();
        var channel = new RedisChannel(RecordingEventChannels.Started, RedisChannel.PatternMode.Literal);
        _queue = await _subscriber.SubscribeAsync(channel);
        logger.LogInformation("Subscribed to {Channel} channel", RecordingEventChannels.Started);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await _queue.ReadAsync(stoppingToken);
                await HandleMessageAsync((string)message.Message!, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process recording-started event; continuing.");
            }
        }
    }

    public async Task HandleMessageAsync(string json, CancellationToken ct = default)
    {
        RecordingStartedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<RecordingStartedEvent>(json);
        }
        catch (JsonException)
        {
            evt = null;
        }

        if (evt is null)
        {
            logger.LogWarning("Failed to deserialize recording-started event; discarding.");
            return;
        }

        var taskId = await enqueue.EnqueueForStartedAsync(evt, ct);
        if (taskId is null)
        {
            logger.LogDebug("No live transcript task created for room {RoomId}", evt.RoomId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_queue is not null)
        {
            _queue.Unsubscribe();
        }

        await base.StopAsync(cancellationToken);
    }
}
