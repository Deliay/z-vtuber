using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;
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
    private readonly IWeiboLoginCookieProvider _loginCookies;
    private readonly CrawlLockProvider _locks;
    private readonly CrawlBackoffRegistry _backoff;
    private readonly WeiboCrawlerOptions _options;
    private readonly ILogger<WeiboTimelineJob> _logger;

    public WeiboTimelineJob(
        IWeiboApiClient api,
        IWeiboSubscriptionService subscription,
        WeiboTimelineRepository repository,
        IWeiboVisitorCookieProvider cookies,
        IWeiboLoginCookieProvider loginCookies,
        CrawlLockProvider locks,
        CrawlBackoffRegistry backoff,
        WeiboCrawlerOptions options,
        ILogger<WeiboTimelineJob> logger) : base(logger)
    {
        _api = api;
        _subscription = subscription;
        _repository = repository;
        _cookies = cookies;
        _loginCookies = loginCookies;
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
        await using var gate = await _locks.AcquireAsync($"weibo:{uid}", ct);
        await CrawlAsync(uid, ct);
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
                    var retried = await HandleRiskControlAsync(uid, sinceId, ex, backoffKey, ct);
                    if (retried is null)
                    {
                        return;
                    }

                    page = retried;
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

    /// <summary>
    /// 处理风控命中。返回重试成功后的页面；返回 <c>null</c> 表示本轮放弃（已登记退避）。
    /// 登录 cookie：宣告失效，200 自愈后重试一次（重试经 WeiboCookieSource 重新 checkout，
    /// 自然拿到刷新后的 cookie）；410/404/504 或重试仍风控则放弃。
    /// 匿名凭证：维持既有 RenewAsync + 退避语义。
    /// </summary>
    private async Task<WeiboTimelinePage?> HandleRiskControlAsync(
        long uid,
        string sinceId,
        WeiboRiskControlException ex,
        string backoffKey,
        CancellationToken ct)
    {
        if (!ex.IsLoginCookie)
        {
            _logger.LogWarning(ex, "Weibo timeline crawl risk control for uid {Uid}; backing off and renewing visitor cookie.", uid);
            _backoff.ReportRiskControl(backoffKey);
            await RenewCookieSafelyAsync(ct);
            return null;
        }

        if (!_options.LoginCookieRetryOnce || string.IsNullOrEmpty(ex.CookieId))
        {
            _logger.LogWarning(ex, "Weibo timeline crawl risk control on login cookie for uid {Uid}; retry disabled or cookieId missing; backing off.", uid);
            _backoff.ReportRiskControl(backoffKey);
            return null;
        }

        ReportInvalidResult report;
        try
        {
            report = await _loginCookies.ReportInvalidAsync(ex.CookieId, ct);
        }
        catch (Exception reportEx)
        {
            _logger.LogWarning(reportEx, "Weibo report-invalid failed for uid {Uid} cookie {CookieId}; backing off.", uid, ex.CookieId);
            _backoff.ReportRiskControl(backoffKey);
            return null;
        }

        if (report.Status != ReportInvalidStatus.Refreshed)
        {
            _logger.LogWarning(
                "Weibo login cookie {CookieId} report-invalid returned {Status} for uid {Uid}; giving up this round.",
                ex.CookieId, report.Status, uid);
            _backoff.ReportRiskControl(backoffKey);
            return null;
        }

        _logger.LogInformation("Weibo login cookie {CookieId} refreshed for uid {Uid}; retrying once.", ex.CookieId, uid);
        try
        {
            return await _api.GetUserTimelineAsync(uid, string.IsNullOrEmpty(sinceId) ? null : sinceId, ct);
        }
        catch (WeiboRiskControlException retryEx)
        {
            // 只宣告一次：重试仍风控不再 report-invalid，直接退避。
            _logger.LogWarning(retryEx, "Weibo retry still hit risk control for uid {Uid}; not reporting again; backing off.", uid);
            _backoff.ReportRiskControl(backoffKey);
            return null;
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
