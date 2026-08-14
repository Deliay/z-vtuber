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
    AccountBasicInfoRepository accountRepository,
    LiveSessionRepository liveSessionRepository,
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
            var root = await bili.GetAccountInfoDataAsync(string.Empty, mid, ct);

            if (root.GetProperty("code").GetInt32() != 200)
            {
                logger.LogWarning("Channel API returned non-200 code for mid {Mid}: {Code}", mid, root.GetProperty("code").GetInt32());
                return;
            }

            var data = root.GetProperty("data");
            var channel = data.GetProperty("channel");
            var roomId = channel.GetProperty("roomId").GetInt64();

            await accountRepository.UpsertAsync(new AccountBasicInfoRecord
            {
                Mid = mid,
                RoomId = roomId,
                Data = BsonConvert.FromJsonElement(channel),
                FetchedAt = DateTime.UtcNow,
            }, ct);

            if (data.TryGetProperty("lives", out var lives) && lives.ValueKind == JsonValueKind.Array)
            {
                await PersistLiveSessionsAsync(roomId, lives, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Account info crawl failed for mid {Mid}.", mid);
        }
    }

    private async Task PersistLiveSessionsAsync(long roomId, JsonElement lives, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var records = new List<LiveSessionRecord>();

        foreach (var live in lives.EnumerateArray())
        {
            var liveId = live.GetProperty("liveId").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(liveId)) continue;

            records.Add(new LiveSessionRecord
            {
                LiveId = liveId,
                RoomId = roomId,
                Title = live.GetProperty("title").GetString() ?? string.Empty,
                CoverUrl = live.TryGetProperty("coverUrl", out var cover) ? cover.GetString() : null,
                ParentArea = live.TryGetProperty("parentArea", out var parent) ? parent.GetString() : null,
                Area = live.TryGetProperty("area", out var area) ? area.GetString() : null,
                IsFinish = live.GetProperty("isFinish").GetBoolean(),
                StartDate = DateTimeOffset.FromUnixTimeMilliseconds(live.GetProperty("startDate").GetInt64()).UtcDateTime,
                StopDate = live.TryGetProperty("stopDate", out var stop) && stop.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(stop.GetInt64()).UtcDateTime
                    : null,
                DanmakuCount = live.GetProperty("danmakusCount").GetInt64(),
                TotalIncome = live.GetProperty("totalIncome").GetDouble(),
                WatchCount = live.GetProperty("watchCount").GetInt64(),
                LikeCount = live.GetProperty("likeCount").GetInt64(),
                PayCount = live.GetProperty("payCount").GetInt64(),
                MaxOnlineCount = live.GetProperty("maxOnlineCount").GetInt64(),
                FetchedAt = now,
            });
        }

        if (records.Count > 0)
        {
            await liveSessionRepository.UpsertManyAsync(records, ct);
            logger.LogInformation("Persisted {Count} live sessions for room {RoomId}.", records.Count, roomId);
        }
    }
}
