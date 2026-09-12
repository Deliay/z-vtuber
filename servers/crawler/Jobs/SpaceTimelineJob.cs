using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Jobs;

public sealed class SpaceTimelineJob : PeriodicJobBase
{
    private readonly IBiliApiClient _bili;
    private readonly CookieServiceClient _cookieServiceClient;
    private readonly ISubscriptionService _subscription;
    private readonly SpaceTimelineRepository _repository;
    private readonly CrawlLockProvider _locks;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ILogger<SpaceTimelineJob> _logger;

    public SpaceTimelineJob(
        IBiliApiClient bili,
        CookieServiceClient cookieServiceClient,
        ISubscriptionService subscription,
        SpaceTimelineRepository repository,
        CrawlLockProvider locks,
        BilibiliCrawlerOptions options,
        ILogger<SpaceTimelineJob> logger) : base(logger)
    {
        _bili = bili;
        _cookieServiceClient = cookieServiceClient;
        _subscription = subscription;
        _repository = repository;
        _locks = locks;
        _options = options;
        _logger = logger;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.SpaceTimelineIntervalSeconds);

    internal override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await _subscription.GetMidsAsync(SubscriptionType.Dynamic, ct);
        foreach (var mid in mids)
        {
            await CrawlLockedAsync(mid, ct);
        }
    }

    private void OnSubscribed(long mid, SubscriptionType type)
    {
        if (type == SubscriptionType.Dynamic)
        {
            _ = CrawlLockedAsync(mid, CancellationToken.None);
        }
    }

    private async Task CrawlLockedAsync(long mid, CancellationToken ct)
    {
        await using var gate = await _locks.AcquireAsync($"space:{mid}", ct);
        await CrawlAsync(mid, ct);
    }

    private async Task CrawlAsync(long mid, CancellationToken ct)
    {
        string cookie;
        try
        {
            var checkout = await _cookieServiceClient.CheckoutAsync(ct);
            cookie = checkout.Cookie;
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
            catch (CrawlBackoffActiveException ex)
            {
                _logger.LogInformation(ex, "Space timeline crawl skipped for mid {Mid}: backoff active.", mid);
                return;
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
                    PubTs = PubTsExtractor.FromJsonElement(item.Data),
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
