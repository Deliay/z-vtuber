using System.Text.Json;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Cookie.Refresh;

public class RedisCookieInvalidEventPublisher : ICookieInvalidEventPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _platform;
    private readonly ICookieAccountInfoRepository? _accountInfoRepository;
    private readonly ICookieSessionRepository? _sessionRepository;
    private readonly ILogger<RedisCookieInvalidEventPublisher> _logger;

    /// <summary>Bilibili 兼容构造：事件帐号信息经 account-info 集合按 cookieId 反查。</summary>
    public RedisCookieInvalidEventPublisher(
        IConnectionMultiplexer redis,
        ICookieAccountInfoRepository accountInfoRepository,
        ILogger<RedisCookieInvalidEventPublisher> logger)
        : this(redis, "bilibili", accountInfoRepository, sessionRepository: null, logger)
    {
    }

    public RedisCookieInvalidEventPublisher(
        IConnectionMultiplexer redis,
        string platform,
        ICookieAccountInfoRepository? accountInfoRepository,
        ICookieSessionRepository? sessionRepository,
        ILogger<RedisCookieInvalidEventPublisher> logger)
    {
        _redis = redis;
        _platform = platform;
        _accountInfoRepository = accountInfoRepository;
        _sessionRepository = sessionRepository;
        _logger = logger;
    }

    public async Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        try
        {
            long? mid = null;
            string? uname = null;

            if (_sessionRepository is not null)
            {
                var session = await _sessionRepository.GetByCookieIdAsync(cookieId, ct);
                if (session is not null)
                {
                    mid = session.Mid;
                    uname = session.Uname;
                }
            }
            else if (_accountInfoRepository is not null && ObjectId.TryParse(cookieId, out var objectId))
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
                Platform = _platform,
            };
            var json = JsonSerializer.Serialize(evt);
            db.Publish(RedisChannel.Literal(CookieEventChannels.Invalid), json);
            _logger.LogDebug("Published cookie invalid event for {Platform} {CookieId}: {Reason}", _platform, cookieId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish cookie invalid event for {CookieId}", cookieId);
        }
    }
}
