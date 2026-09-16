using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;

namespace Z.Vtuber.RecordingTranscript.Consumer.Events;

/// <summary>订阅 recording-finished，对漏建场次补建 mode=file 任务（已存在 live 任务时幂等跳过）。</summary>
public sealed class RecordingFinishedSubscriber(
    IConnectionMultiplexer redis,
    TranscriptEnqueueService enqueue,
    ILogger<RecordingFinishedSubscriber> logger) : BackgroundService
{
    private ISubscriber? _subscriber;
    private ChannelMessageQueue? _queue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriber = redis.GetSubscriber();
        var channel = new RedisChannel(RecordingEventChannels.Finished, RedisChannel.PatternMode.Literal);
        _queue = await _subscriber.SubscribeAsync(channel);
        logger.LogInformation("Subscribed to {Channel} channel", RecordingEventChannels.Finished);

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
                logger.LogWarning(ex, "Failed to process recording-finished event; continuing.");
            }
        }
    }

    public async Task HandleMessageAsync(string json, CancellationToken ct = default)
    {
        RecordingFinishedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<RecordingFinishedEvent>(json);
        }
        catch (JsonException)
        {
            evt = null;
        }

        if (evt is null)
        {
            logger.LogWarning("Failed to deserialize recording-finished event; discarding.");
            return;
        }

        await enqueue.EnqueueForFinishedAsync(evt, ct);
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
