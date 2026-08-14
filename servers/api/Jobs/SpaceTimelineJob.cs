using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class SpaceTimelineJob : PeriodicJobBase
{
    private readonly IBiliApiClient _bili;
    private readonly ICookiePoolService _cookiePool;
    private readonly ISubscriptionService _subscription;
    private readonly SpaceTimelineRepository _repository;
    private readonly CrawlLockProvider _locks;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ILogger<SpaceTimelineJob> _logger;

    public SpaceTimelineJob(
        IBiliApiClient bili,
        ICookiePoolService cookiePool,
        ISubscriptionService subscription,
        SpaceTimelineRepository repository,
        CrawlLockProvider locks,
        BilibiliCrawlerOptions options,
        ILogger<SpaceTimelineJob> logger) : base(logger)
    {
        _bili = bili;
        _cookiePool = cookiePool;
        _subscription = subscription;
        _repository = repository;
        _locks = locks;
        _options = options;
        _logger = logger;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.SpaceTimelineIntervalSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await _subscription.GetMidsAsync(ct);
        foreach (var mid in mids)
        {
            await CrawlLockedAsync(mid, ct);
        }
    }

    private void OnSubscribed(long mid) => _ = CrawlLockedAsync(mid, CancellationToken.None);

    private async Task CrawlLockedAsync(long mid, CancellationToken ct)
    {
        var gate = _locks.GetLock($"space:{mid}");
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

    private async Task CrawlAsync(long mid, CancellationToken ct)
    {
        string cookie;
        try
        {
            cookie = (await _cookiePool.NextAsync(ct)).Cookie;
        }
        catch (NoAvailableCookieException)
        {
            _logger.LogWarning("Space timeline crawl skipped for mid {Mid}: no active cookie.", mid);
            return;
        }

        var offset = string.Empty;
        var visitedOffsets = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            SpaceDynamicsPage page;
            try
            {
                page = await _bili.GetSpaceDynamicsAsync(cookie, mid, offset, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Space timeline fetch failed for mid {Mid} at offset {Offset}.", mid, offset);
                return;
            }

            if (page.Items.Count == 0)
            {
                return;
            }

            var ids = page.Items.Select(i => i.DynamicId).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var existing = await _repository.GetExistingDynamicIdsAsync(ids, ct);

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
                await _repository.BulkUpsertAsync(toInsert, ct);
            }

            if (!SpaceTimelinePaging.ShouldContinuePaging(ids.Count, existing.Count, page.HasMore, page.Offset))
            {
                return;
            }

            if (!visitedOffsets.Add(page.Offset))
            {
                _logger.LogWarning("Space timeline paging loop detected for mid {Mid}; stopping.", mid);
                return;
            }

            offset = page.Offset;
        }
    }
}
