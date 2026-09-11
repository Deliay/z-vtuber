using System.Text.Json;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Services;

public sealed class RedisCookieInvalidEventPublisher(
    IConnectionMultiplexer redis,
    ICookieAccountInfoRepository accountInfoRepository,
    ILogger<RedisCookieInvalidEventPublisher> logger) : ICookieInvalidEventPublisher
{
    public async Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        try
        {
            // 反查账号信息
            long? mid = null;
            string? uname = null;
            if (MongoDB.Bson.ObjectId.TryParse(cookieId, out var objectId))
            {
                var accounts = await accountInfoRepository.GetByCookieIdsAsync([objectId], ct);
                if (accounts.TryGetValue(objectId, out var account))
                {
                    mid = account.Mid;
                    uname = account.Uname;
                }
            }

            var evt = new CookieInvalidEvent
            {
                CookieId = cookieId,
                Mid = mid,
                Uname = uname,
                OccurredAt = DateTime.UtcNow,
                Reason = reason,
            };

            var channel = new RedisChannel(CookieEventChannels.Invalid, RedisChannel.PatternMode.Literal);
            await redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(evt));
        }
        catch (Exception ex)
        {
            // 发布失败仅记日志，不影响落库与长连接
            logger.LogWarning(ex, "Failed to publish cookie invalid event for cookie {CookieId}; continuing.", cookieId);
        }
    }
}
