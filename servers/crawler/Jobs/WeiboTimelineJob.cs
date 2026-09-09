using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Weibo;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Jobs;

public sealed class WeiboTimelineJob : PeriodicJobBase
{
    private readonly IWeiboApiClient _api;
    private readonly IWeiboSubscriptionService _subscription;
    private readonly WeiboTimelineRepository _repository;
    private readonly IWeiboVisitorCookieProvider _cookies;
    private readonly CrawlLockProvider _locks;
    private readonly ICrawlBackoffRegistry _backoff;
    private readonly WeiboCrawlerOptions _options;
    private readonly ILogger<WeiboTimelineJob> _logger;

    public WeiboTimelineJob(
        IWeiboApiClient api,
        IWeiboSubscriptionService subscription,
        WeiboTimelineRepository repository,
        IWeiboVisitorCookieProvider cookies,
        CrawlLockProvider locks,
        ICrawlBackoffRegistry backoff,
        WeiboCrawlerOptions options,
        ILogger<WeiboTimelineJob> logger) : base(logger)
    {
        _api = api;
        _subscription = subscription;
        _repository = repository;
        _cookies = cookies;
        _locks = locks;
        _backoff = backoff;
        _options = options;
        _logger = logger;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.TimelineIntervalSeconds);

    internal override async Task RunOnceAsync(CancellationToken ct)
    {
        var uids = await _subscription.GetUidsAsync(ct);
        foreach (var uid in uids)
        {
            await CrawlLockedAsync(uid, ct);
        }
    }

    private void OnSubscribed(long uid)
    {
        _ = CrawlLockedAsync(uid, CancellationToken.None);
    }

    private async Task CrawlLockedAsync(long uid, CancellationToken ct)
    {
        var gate = _locks.GetLock($"weibo:{uid}");
        await gate.WaitAsync(ct);
        try
        {
            await CrawlAsync(uid, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CrawlAsync(long uid, CancellationToken ct)
    {
        var backoffKey = $"weibo:{uid}";

        if (!_backoff.TryAcquire(backoffKey, out var wait))
        {
            _logger.LogInformation("Weibo timeline crawl skipped for uid {Uid}: backoff active ({Wait}).", uid, wait);
            return;
        }

        var sinceId = string.Empty;
        var visitedSinceIds = new HashSet<string>(StringComparer.Ordinal);
        var pageCount = 0;

        while (true)
        {
                WeiboTimelinePage page;
                try
                {
                    page = await _api.GetUserTimelineAsync(uid, string.IsNullOrEmpty(sinceId) ? null : sinceId, ct);
                }
                catch (WeiboRiskControlException ex)
                {
                    _logger.LogWarning(ex, "Weibo timeline crawl risk control for uid {Uid}; backing off and renewing visitor cookie.", uid);
                    _backoff.ReportRiskControl(backoffKey);
                    await RenewCookieSafelyAsync(ct);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Weibo timeline fetch failed for uid {Uid} at sinceId {SinceId}.", uid, sinceId);
                    return;
                }

                if (page.Items.Count == 0)
                {
                    _backoff.ReportSuccess(backoffKey);
                    return;
                }

                var ids = page.Items.Select(i => i.MblogId).Where(id => !string.IsNullOrEmpty(id)).ToList();
                var existing = await _repository.GetExistingMblogIdsAsync(ids, ct);

                var toInsert = new List<WeiboTimelineRecord>();
                foreach (var item in page.Items)
                {
                    if (string.IsNullOrEmpty(item.MblogId) || existing.Contains(item.MblogId))
                    {
                        continue;
                    }

                    toInsert.Add(new WeiboTimelineRecord
                    {
                        Uid = uid,
                        MblogId = item.MblogId,
                        Data = BsonConvert.FromJsonElement(item.Data),
                        PubTs = item.PubTs,
                        FetchedAt = DateTime.UtcNow,
                    });
                }

                if (toInsert.Count > 0)
                {
                    await _repository.BulkUpsertAsync(toInsert, ct);
                }

                pageCount++;

                // 本页全部已存在即收敛；或未提示更多；或无游标；或达到页数上限
                if (existing.Count >= ids.Count
                    || !page.HasMore
                    || string.IsNullOrEmpty(page.SinceId)
                    || pageCount >= _options.MaxPagesPerCrawl)
                {
                    _backoff.ReportSuccess(backoffKey);
                    return;
                }

                // 防游标循环
                if (!visitedSinceIds.Add(page.SinceId))
                {
                    _logger.LogWarning("Weibo timeline paging loop detected for uid {Uid}; stopping.", uid);
                    _backoff.ReportSuccess(backoffKey);
                    return;
                }

                sinceId = page.SinceId;
            }
    }

    private async Task RenewCookieSafelyAsync(CancellationToken ct)
    {
        try
        {
            await _cookies.RenewAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Weibo visitor cookie renewal failed; will retry on next crawl.");
        }
    }
}
