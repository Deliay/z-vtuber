using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class AccountInfoJob(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    ISubscriptionService subscription,
    AccountBasicInfoRepository repository,
    CrawlLockProvider locks,
    BilibiliCrawlerOptions options,
    ILogger<AccountInfoJob> logger) : PeriodicJobBase(logger)
{
    protected override TimeSpan Interval => TimeSpan.FromHours(options.AccountInfoIntervalHours);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await subscription.GetMidsAsync(ct);
        foreach (var mid in mids)
        {
            var key = $"account:{mid}";
            var gate = locks.GetLock(key);
            await gate.WaitAsync(ct);
            try
            {
                await CrawlAsync(mid, ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task CrawlAsync(long mid, CancellationToken ct)
    {
        try
        {
            var cookie = await cookiePool.NextAsync(ct);
            var data = await bili.GetAccountInfoDataAsync(cookie.Cookie, mid, ct);

            var roomId = ExtractRoomId(data);

            await repository.InsertAsync(new AccountBasicInfoRecord
            {
                Mid = mid,
                RoomId = roomId,
                Data = BsonConvert.FromJsonElement(data),
                FetchedAt = DateTime.UtcNow,
            }, ct);
        }
        catch (NoAvailableCookieException)
        {
            logger.LogWarning("Account info crawl skipped for mid {Mid}: no active cookie.", mid);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Account info crawl failed for mid {Mid}.", mid);
        }
    }

    private static long ExtractRoomId(System.Text.Json.JsonElement data)
    {
        if (data.TryGetProperty("live_room", out var liveRoom)
            && liveRoom.TryGetProperty("roomid", out var roomId)
            && roomId.ValueKind == System.Text.Json.JsonValueKind.Number
            && roomId.TryGetInt64(out var value))
        {
            return value;
        }

        return 0;
    }
}
