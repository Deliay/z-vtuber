using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class LiveStatusJob(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    ISubscriptionService subscription,
    AccountBasicInfoRepository accountRepository,
    LiveStatusRepository repository,
    CrawlLockProvider locks,
    ILiveSessionTracker sessionTracker,
    BilibiliCrawlerOptions options,
    ILogger<LiveStatusJob> logger) : PeriodicJobBase(logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.StatusIntervalSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await subscription.GetMidsAsync(ct);
        foreach (var mid in mids)
        {
            var roomId = await accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
            if (roomId is null || roomId <= 0)
            {
                continue;
            }

            var gate = locks.GetLock($"live:{roomId}");
            await gate.WaitAsync(ct);
            try
            {
                await CrawlAsync(roomId.Value, ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task CrawlAsync(long roomId, CancellationToken ct)
    {
        try
        {
            var cookie = await cookiePool.NextAsync(ct);
            var roomInfo = await bili.GetLiveRoomInfoAsync(cookie.Cookie, roomId, ct);

            var title = roomInfo.Title ?? string.Empty;
            var liveStatus = roomInfo.LiveStatus == 1;

            var latest = await repository.GetLatestByRoomIdAsync(roomId, ct);
            var (shouldPersist, sessionId) = LiveStatusStateMachine.Decide(latest, title, liveStatus);

            sessionTracker.SetCurrentSessionId(roomId, sessionId);

            if (!shouldPersist)
            {
                return;
            }

            await repository.InsertAsync(new LiveStatusRecord
            {
                RoomId = roomId,
                Title = title,
                LiveStatus = liveStatus,
                LiveSessionId = sessionId,
                CreatedAt = DateTime.UtcNow,
            }, ct);
        }
        catch (NoAvailableCookieException)
        {
            logger.LogWarning("Live status crawl skipped for room {RoomId}: no active cookie.", roomId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Live status crawl failed for room {RoomId}.", roomId);
        }
    }
}
