using System.Text.Json;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Cookie.Refresh;

public class RedisCookieInvalidEventPublisher : ICookieInvalidEventPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ICookieAccountInfoRepository _accountInfoRepository;
    private readonly ILogger<RedisCookieInvalidEventPublisher> _logger;

    public RedisCookieInvalidEventPublisher(
        IConnectionMultiplexer redis,
        ICookieAccountInfoRepository accountInfoRepository,
        ILogger<RedisCookieInvalidEventPublisher> logger)
    {
        _redis = redis;
        _accountInfoRepository = accountInfoRepository;
        _logger = logger;
    }

    public async Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        try
        {
            long? mid = null;
            string? uname = null;

            if (ObjectId.TryParse(cookieId, out var objectId))
            {
                var accountInfo = await _accountInfoRepository.GetByCookieIdsAsync(
                    new[] { objectId }, ct);
                if (accountInfo.TryGetValue(objectId, out var info))
                {
                    mid = info.Mid;
                    uname = info.Uname;
                }
            }

            var db = _redis.GetDatabase();
            var evt = new CookieInvalidEvent
            {
                CookieId = cookieId,
                Reason = reason,
                Mid = mid,
                Uname = uname,
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
    }
}
