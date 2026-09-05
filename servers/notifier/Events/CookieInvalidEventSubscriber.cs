using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Notifier.Dispatch;

namespace Z.Vtuber.Notifier.Events;

public interface ICookieInvalidEventSubscriber
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed class CookieInvalidEventSubscriber : ICookieInvalidEventSubscriber
{
    private readonly IConnectionMultiplexer _redis;
    private readonly INotificationDispatchService _dispatchService;
    private readonly ILogger<CookieInvalidEventSubscriber> _logger;
    private ISubscriber? _subscriber;
    private ChannelMessageQueue? _queue;

    public CookieInvalidEventSubscriber(
        IConnectionMultiplexer redis,
        INotificationDispatchService dispatchService,
        ILogger<CookieInvalidEventSubscriber> logger)
    {
        _redis = redis;
        _dispatchService = dispatchService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _subscriber = _redis.GetSubscriber();
        var channel = new RedisChannel(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);
        _queue = await _subscriber.SubscribeAsync(channel);

        _logger.LogInformation("Subscribed to cookie-invalid channel");

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var message = await _queue.ReadAsync(ct);
                    var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)message.Message!);
                    if (evt is null)
                    {
                        _logger.LogWarning("Failed to deserialize cookie invalid event; discarding.");
                        continue;
                    }

                    await _dispatchService.DispatchAsync(evt, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process cookie invalid event; continuing.");
                }
            }
        }, ct);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_queue is not null)
        {
            _queue.Unsubscribe();
            _logger.LogInformation("Unsubscribed from cookie-invalid channel");
        }

        return Task.CompletedTask;
    }
}
