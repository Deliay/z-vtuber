using System.Text.Json;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Jobs;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public interface IAccountInfoCrawler
{
    Task CrawlAsync(long mid, CancellationToken ct = default);
}

public sealed class AccountInfoCrawler(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    AccountBasicInfoRepository repository,
    CrawlLockProvider locks,
    ILogger<AccountInfoCrawler> logger) : IAccountInfoCrawler
{
    public async Task CrawlAsync(long mid, CancellationToken ct = default)
    {
        var gate = locks.GetLock($"account:{mid}");
        await gate.WaitAsync(ct);
        try
        {
            await CrawlCoreAsync(mid, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CrawlCoreAsync(long mid, CancellationToken ct)
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

    internal static long ExtractRoomId(JsonElement data)
    {
        if (data.TryGetProperty("live_room", out var liveRoom)
            && liveRoom.TryGetProperty("roomid", out var roomId)
            && roomId.ValueKind == JsonValueKind.Number
            && roomId.TryGetInt64(out var value))
        {
            return value;
        }

        return 0;
    }
}
