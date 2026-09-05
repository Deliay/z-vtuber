using System.Text.Json;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Notifier.Dispatch;

namespace Z.Vtuber.Notifier.Events;

public sealed class CookieInvalidEventSubscriber : BackgroundService
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriber = _redis.GetSubscriber();
        var channel = new RedisChannel(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);
        _queue = await _subscriber.SubscribeAsync(channel);

        _logger.LogInformation("Subscribed to cookie-invalid channel");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await _queue.ReadAsync(stoppingToken);
                var evt = JsonSerializer.Deserialize<CookieInvalidEvent>((string)message.Message!);
                if (evt is null)
                {
                    _logger.LogWarning("Failed to deserialize cookie invalid event; discarding.");
                    continue;
                }

                await _dispatchService.DispatchAsync(evt, stoppingToken);
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
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_queue is not null)
        {
            _queue.Unsubscribe();
            _logger.LogInformation("Unsubscribed from cookie-invalid channel");
        }

        await base.StopAsync(cancellationToken);
    }
}
