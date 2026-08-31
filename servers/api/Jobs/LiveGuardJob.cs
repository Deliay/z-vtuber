using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public class LiveGuardJob : PeriodicJobBase
{
    private const int BeijingOffsetHours = 8;

    private readonly ISubscriptionService _subscription;
    private readonly IBiliApiClient _bili;
    private readonly ICookiePoolService _cookiePool;
    private readonly AccountBasicInfoRepository _accountRepository;
    private readonly LiveGuardRepository _guardRepository;
    private readonly IAccountInfoCrawler _accountInfoCrawler;
    private readonly CrawlLockProvider _locks;
    private readonly BilibiliCrawlerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random = Random.Shared;
    private readonly ILogger<LiveGuardJob> _logger;

    public LiveGuardJob(
        ISubscriptionService subscription,
        IBiliApiClient bili,
        ICookiePoolService cookiePool,
        AccountBasicInfoRepository accountRepository,
        LiveGuardRepository guardRepository,
        IAccountInfoCrawler accountInfoCrawler,
        CrawlLockProvider locks,
        BilibiliCrawlerOptions options,
        TimeProvider timeProvider,
        ILogger<LiveGuardJob> logger) : base(logger)
    {
        _subscription = subscription;
        _bili = bili;
        _cookiePool = cookiePool;
        _accountRepository = accountRepository;
        _guardRepository = guardRepository;
        _accountInfoCrawler = accountInfoCrawler;
        _locks = locks;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
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
        if (type == SubscriptionType.Guard)
        {
            _ = RunOnceAsync(CancellationToken.None);
        }
    }

    private async Task CrawlLockedAsync(long mid, string date, CancellationToken ct)
    {
        var gate = _locks.GetLock($"guard:{mid}");
        await gate.WaitAsync(ct);
        try
        {
            await CrawlAsync(mid, date, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CrawlAsync(long mid, string date, CancellationToken ct)
    {
        string cookie;
        try
        {
            cookie = (await _cookiePool.NextAsync(ct)).Cookie;
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

        var entries = await FetchAllPagesAsync(cookie, mid, roomId.Value, ct);
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

    private async Task<List<LiveGuardEntry>?> FetchAllPagesAsync(string cookie, long mid, long roomId, CancellationToken ct)
    {
        var merged = new Dictionary<long, LiveGuardEntry>();
        var page = 1;

        while (page <= _options.GuardMaxPages)
        {
            GuardInfo info;
            try
            {
                info = await _bili.GetRoomGuardListAsync(cookie, roomId, mid, page, ct);
            }
            catch (CrawlBackoffActiveException ex)
            {
                _logger.LogInformation(ex, "Guard crawl skipped for mid {Mid}: backoff active.", mid);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guard crawl failed for mid {Mid} at page {Page}; abandoning this round.", mid, page);
                return null;
            }

            foreach (var user in info.List)
            {
                merged.TryAdd(user.Uid, ToEntry(user));
            }

            if (info.List.Count == 0 || page >= info.Info.PageCount || page >= _options.GuardMaxPages)
            {
                break;
            }

            await DelayBetweenPagesAsync(ct);
            page++;
        }

        return merged.Values.ToList();
    }

    internal virtual Task DelayBetweenPagesAsync(CancellationToken ct)
    {
        var seconds = _random.Next(_options.GuardPageDelayMinSeconds, _options.GuardPageDelayMaxSeconds + 1);
        return Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }

    private static LiveGuardEntry ToEntry(GuardUserInfo user) => new()
    {
        Uid = user.Uid,
        UserName = user.UserName,
        GuardLevel = user.GuardType,
        MedalName = string.IsNullOrEmpty(user.MedalInfo.Name) ? null : user.MedalInfo.Name,
        MedalLevel = user.MedalInfo.Level,
    };

    private DateTime BeijingNow() => _timeProvider.GetUtcNow().UtcDateTime.AddHours(BeijingOffsetHours);
}