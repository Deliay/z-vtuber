using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class SpaceTimelineJob(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    ISubscriptionService subscription,
    SpaceTimelineRepository repository,
    CrawlLockProvider locks,
    BilibiliCrawlerOptions options,
    ILogger<SpaceTimelineJob> logger) : PeriodicJobBase(logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.SpaceTimelineIntervalSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await subscription.GetMidsAsync(ct);
        foreach (var mid in mids)
        {
            var gate = locks.GetLock($"space:{mid}");
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
        string cookie;
        try
        {
            cookie = (await cookiePool.NextAsync(ct)).Cookie;
        }
        catch (NoAvailableCookieException)
        {
            logger.LogWarning("Space timeline crawl skipped for mid {Mid}: no active cookie.", mid);
            return;
        }

        var offset = string.Empty;
        var visitedOffsets = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            SpaceDynamicsPage page;
            try
            {
                page = await bili.GetSpaceDynamicsAsync(cookie, mid, offset, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Space timeline fetch failed for mid {Mid} at offset {Offset}.", mid, offset);
                return;
            }

            if (page.Items.Count == 0)
            {
                return;
            }

            var ids = page.Items.Select(i => i.DynamicId).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var existing = await repository.GetExistingDynamicIdsAsync(ids, ct);

            var toInsert = new List<SpaceTimelineRecord>();
            foreach (var item in page.Items)
            {
                if (string.IsNullOrEmpty(item.DynamicId) || existing.Contains(item.DynamicId))
                {
                    continue;
                }

                toInsert.Add(new SpaceTimelineRecord
                {
                    Mid = mid,
                    DynamicId = item.DynamicId,
                    Data = BsonConvert.FromJsonElement(item.Data),
                    FetchedAt = DateTime.UtcNow,
                });
            }

            if (toInsert.Count > 0)
            {
                await repository.BulkUpsertAsync(toInsert, ct);
            }

            if (!SpaceTimelinePaging.ShouldContinuePaging(ids.Count, existing.Count, page.HasMore, page.Offset))
            {
                return;
            }

            if (!visitedOffsets.Add(page.Offset))
            {
                logger.LogWarning("Space timeline paging loop detected for mid {Mid}; stopping.", mid);
                return;
            }

            offset = page.Offset;
        }
    }
}
