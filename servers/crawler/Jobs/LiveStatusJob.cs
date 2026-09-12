using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Jobs;

public sealed class LiveStatusJob : PeriodicJobBase
{
    private readonly IBiliApiClient _bili;
    private readonly CookieServiceClient _cookieServiceClient;
    private readonly ISubscriptionService _subscription;
    private readonly IAccountInfoCrawler _accountInfoCrawler;
    private readonly AccountBasicInfoRepository _accountRepository;
    private readonly LiveStatusRepository _repository;
    private readonly LiveSessionRepository _sessionRepository;
    private readonly CrawlLockProvider _locks;
    private readonly ILiveSessionTracker _sessionTracker;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ILogger<LiveStatusJob> _logger;

    public LiveStatusJob(
        IBiliApiClient bili,
        CookieServiceClient cookieServiceClient,
        ISubscriptionService subscription,
        IAccountInfoCrawler accountInfoCrawler,
        AccountBasicInfoRepository accountRepository,
        LiveStatusRepository repository,
        LiveSessionRepository sessionRepository,
        CrawlLockProvider locks,
        ILiveSessionTracker sessionTracker,
        BilibiliCrawlerOptions options,
        ILogger<LiveStatusJob> logger) : base(logger)
    {
        _bili = bili;
        _cookieServiceClient = cookieServiceClient;
        _subscription = subscription;
        _accountInfoCrawler = accountInfoCrawler;
        _accountRepository = accountRepository;
        _repository = repository;
        _sessionRepository = sessionRepository;
        _locks = locks;
        _sessionTracker = sessionTracker;
        _options = options;
        _logger = logger;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.StatusIntervalSeconds);

    internal override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await _subscription.GetMidsAsync(SubscriptionType.Danmaku, ct);
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

    private void OnSubscribed(long mid, SubscriptionType type)
    {
        if (type == SubscriptionType.Danmaku)
        {
            _ = CrawlLiveImmediatelyAsync(mid);
        }
    }

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
        await using var gate = await _locks.AcquireAsync($"live:{roomId}", ct);
        await CrawlAsync(roomId, ct);
    }

    private async Task CrawlAsync(long roomId, CancellationToken ct)
    {
        try
        {
            var checkout = await _cookieServiceClient.CheckoutAsync(ct);
            var roomInfo = await _bili.GetLiveRoomInfoAsync(checkout.Cookie, roomId, ct);

            var title = roomInfo.Title ?? string.Empty;
            var liveStatus = roomInfo.LiveStatus == 1;

            var latest = await _repository.GetLatestByRoomIdAsync(roomId, ct);
            var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(latest, title, liveStatus);

            _sessionTracker.SetCurrentSessionId(roomId, sessionId);

            if (!shouldPersist)
            {
                return;
            }

            await MaintainSessionAsync(roomId, title, sessionId, transition, ct);

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
        catch (CrawlBackoffActiveException ex)
        {
            _logger.LogInformation(ex, "Live status crawl skipped for room {RoomId}: backoff active.", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live status crawl failed for room {RoomId}.", roomId);
        }
    }

    private async Task MaintainSessionAsync(
        long roomId, string title, string? sessionId, SessionTransition transition, CancellationToken ct)
    {
        if (sessionId is null)
        {
            return;
        }

        try
        {
            switch (transition)
            {
                case SessionTransition.Started:
                    await _sessionRepository.UpsertAsync(new LiveSessionRecord
                    {
                        LiveId = sessionId,
                        RoomId = roomId,
                        Title = title,
                        IsFinish = false,
                        StartDate = DateTime.UtcNow,
                        FetchedAt = DateTime.UtcNow,
                    }, ct);
                    break;

                case SessionTransition.Ended:
                    await _sessionRepository.CloseAsync(sessionId, DateTime.UtcNow, ct);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to maintain live session for room {RoomId}; session write skipped.", roomId);
        }
    }
}
