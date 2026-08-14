using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class LiveStatusJob : PeriodicJobBase
{
    private readonly IBiliApiClient _bili;
    private readonly ICookiePoolService _cookiePool;
    private readonly ISubscriptionService _subscription;
    private readonly IAccountInfoCrawler _accountInfoCrawler;
    private readonly AccountBasicInfoRepository _accountRepository;
    private readonly LiveStatusRepository _repository;
    private readonly CrawlLockProvider _locks;
    private readonly ILiveSessionTracker _sessionTracker;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ILogger<LiveStatusJob> _logger;

    public LiveStatusJob(
        IBiliApiClient bili,
        ICookiePoolService cookiePool,
        ISubscriptionService subscription,
        IAccountInfoCrawler accountInfoCrawler,
        AccountBasicInfoRepository accountRepository,
        LiveStatusRepository repository,
        CrawlLockProvider locks,
        ILiveSessionTracker sessionTracker,
        BilibiliCrawlerOptions options,
        ILogger<LiveStatusJob> logger) : base(logger)
    {
        _bili = bili;
        _cookiePool = cookiePool;
        _subscription = subscription;
        _accountInfoCrawler = accountInfoCrawler;
        _accountRepository = accountRepository;
        _repository = repository;
        _locks = locks;
        _sessionTracker = sessionTracker;
        _options = options;
        _logger = logger;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.StatusIntervalSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await _subscription.GetMidsAsync(ct);
        foreach (var mid in mids)
        {
            var roomId = await _accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
            if (roomId is null || roomId <= 0)
            {
                continue;
            }

            await CrawlLockedAsync(roomId.Value, ct);
        }
    }

    private void OnSubscribed(long mid) => _ = CrawlLiveImmediatelyAsync(mid);

    private async Task CrawlLiveImmediatelyAsync(long mid)
    {
        await _accountInfoCrawler.CrawlAsync(mid);

        var roomId = await _accountRepository.GetLatestRoomIdByMidAsync(mid);
        if (roomId is null || roomId <= 0)
        {
            _logger.LogWarning("Live status immediate crawl skipped for mid {Mid}: account info not available.", mid);
            return;
        }

        await CrawlLockedAsync(roomId.Value, CancellationToken.None);
    }

    private async Task CrawlLockedAsync(long roomId, CancellationToken ct)
    {
        var gate = _locks.GetLock($"live:{roomId}");
        await gate.WaitAsync(ct);
        try
        {
            await CrawlAsync(roomId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CrawlAsync(long roomId, CancellationToken ct)
    {
        try
        {
            var cookie = await _cookiePool.NextAsync(ct);
            var roomInfo = await _bili.GetLiveRoomInfoAsync(cookie.Cookie, roomId, ct);

            var title = roomInfo.Title ?? string.Empty;
            var liveStatus = roomInfo.LiveStatus == 1;

            var latest = await _repository.GetLatestByRoomIdAsync(roomId, ct);
            var (shouldPersist, sessionId) = LiveStatusStateMachine.Decide(latest, title, liveStatus);

            _sessionTracker.SetCurrentSessionId(roomId, sessionId);

            if (!shouldPersist)
            {
                return;
            }

            await _repository.InsertAsync(new LiveStatusRecord
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
            _logger.LogWarning("Live status crawl skipped for room {RoomId}: no active cookie.", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live status crawl failed for room {RoomId}.", roomId);
        }
    }
}
