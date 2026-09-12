using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Jobs;

public class LiveGuardJob : PeriodicJobBase
{
    private const int BeijingOffsetHours = 8;

    private readonly ISubscriptionService _subscription;
    private readonly ICookieServiceClient _cookieServiceClient;
    private readonly AccountBasicInfoRepository _accountRepository;
    private readonly LiveGuardRepository _guardRepository;
    private readonly IAccountInfoCrawler _accountInfoCrawler;
    private readonly CrawlLockProvider _locks;
    private readonly BilibiliCrawlerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LiveGuardJob> _logger;
    private readonly LiveGuardListFetcher _fetcher;

    public LiveGuardJob(
        ISubscriptionService subscription,
        ICookieServiceClient cookieServiceClient,
        AccountBasicInfoRepository accountRepository,
        LiveGuardRepository guardRepository,
        IAccountInfoCrawler accountInfoCrawler,
        CrawlLockProvider locks,
        BilibiliCrawlerOptions options,
        TimeProvider timeProvider,
        ILogger<LiveGuardJob> logger,
        LiveGuardListFetcher fetcher) : base(logger)
    {
        _subscription = subscription;
        _cookieServiceClient = cookieServiceClient;
        _accountRepository = accountRepository;
        _guardRepository = guardRepository;
        _accountInfoCrawler = accountInfoCrawler;
        _locks = locks;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _fetcher = fetcher;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(_options.GuardCheckIntervalMinutes);

    internal override async Task RunOnceAsync(CancellationToken ct)
    {
        var now = BeijingNow();
        if (now.Hour < _options.GuardDailyCrawlHour)
        {
            _logger.LogDebug("Guard crawl idle: Beijing now {Now} before hour {Hour}.", now, _options.GuardDailyCrawlHour);
            return;
        }

        var date = now.ToString("yyyy-MM-dd");
        var subscribed = await _subscription.GetMidsAsync(SubscriptionType.Guard, ct);
        var withSnapshot = await _guardRepository.GetMidsWithSnapshotAsync(date, ct);
        var pending = subscribed.Except(withSnapshot).ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("Guard crawl for {Date}: no pending mids (all subscribed already snapshotted).", date);
            return;
        }

        _logger.LogInformation("Guard crawl for {Date}: {Count} pending mids.", date, pending.Count);
        foreach (var mid in pending)
        {
            await CrawlLockedAsync(mid, date, ct);
        }
    }

    private void OnSubscribed(long mid, SubscriptionType type)
    {
        if (type != SubscriptionType.Guard)
        {
            return;
        }

        // 凌晨窗口（北京时间 00:00–GuardCatchUpBeforeHour）新增的 Guard 订阅：
        // 立即补抓一次并视为前一日的舰长快照；其余时间收敛到 RunOnceAsync 的差集检查路径。
        if (BeijingNow().Hour < _options.GuardCatchUpBeforeHour)
        {
            _ = CatchUpYesterdayAsync(mid);
        }
        else
        {
            _ = RunOnceAsync(CancellationToken.None);
        }
    }

    private async Task CatchUpYesterdayAsync(long mid)
    {
        try
        {
            var date = BeijingNow().AddDays(-1).ToString("yyyy-MM-dd");
            var withSnapshot = await _guardRepository.GetMidsWithSnapshotAsync(date, CancellationToken.None);
            if (withSnapshot.Contains(mid))
            {
                _logger.LogInformation("Guard catch-up skipped for mid {Mid}: snapshot for {Date} already exists.", mid, date);
                return;
            }

            _logger.LogInformation("Guard catch-up for mid {Mid}: crawling as {Date} snapshot.", mid, date);
            await CrawlLockedAsync(mid, date, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guard catch-up failed for mid {Mid}.", mid);
        }
    }

    private async Task CrawlLockedAsync(long mid, string date, CancellationToken ct)
    {
        await using var gate = await _locks.AcquireAsync($"guard:{mid}", ct);
        await CrawlAsync(mid, date, ct);
    }

    private async Task CrawlAsync(long mid, string date, CancellationToken ct)
    {
        string cookieId;
        string cookie;
        try
        {
            var checkout = await _cookieServiceClient.CheckoutAsync(ct);
            cookieId = checkout.CookieId;
            cookie = checkout.Cookie;
        }
        catch (NoAvailableCookieException)
        {
            _logger.LogWarning("Guard crawl skipped for mid {Mid}: no active cookie.", mid);
            return;
        }

        var roomId = await _accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
        if (roomId is null)
        {
            await _accountInfoCrawler.CrawlAsync(mid, ct);
            roomId = await _accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
            if (roomId is null)
            {
                _logger.LogWarning("Guard crawl skipped for mid {Mid}: no roomId even after on-demand crawl.", mid);
                return;
            }
        }

        var entries = await _fetcher.FetchAllAsync(cookieId, cookie, mid, roomId.Value, ct);
        if (entries is null)
        {
            return;
        }

        var record = new LiveGuardRecord
        {
            Mid = mid,
            RoomId = roomId.Value,
            Date = date,
            Guards = entries,
            GuardCount = entries.Count,
            FetchedAt = DateTime.UtcNow,
        };
        await _guardRepository.UpsertDailySnapshotAsync(record, ct);
        _logger.LogInformation("Guard snapshot persisted for mid {Mid} on {Date}: {Count} guards.", mid, date, entries.Count);
    }

    private DateTime BeijingNow() => _timeProvider.GetUtcNow().UtcDateTime.AddHours(BeijingOffsetHours);
}