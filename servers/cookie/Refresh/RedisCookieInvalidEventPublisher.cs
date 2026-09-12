using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;

namespace Z.Vtuber.Cookie.Refresh;

public class RedisCookieInvalidEventPublisher : ICookieInvalidEventPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCookieInvalidEventPublisher> _logger;

    public RedisCookieInvalidEventPublisher(
        IConnectionMultiplexer redis,
        ILogger<RedisCookieInvalidEventPublisher> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var evt = new CookieInvalidEvent
            {
                CookieId = cookieId,
                Reason = reason,
                OccurredAt = DateTime.UtcNow,
            };
            var json = JsonSerializer.Serialize(evt);
            db.Publish(RedisChannel.Literal(CookieEventChannels.Invalid), json);
            _logger.LogDebug("Published cookie invalid event for {CookieId}: {Reason}", cookieId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish cookie invalid event for {CookieId}", cookieId);
        }

        return Task.CompletedTask;
    }
}
