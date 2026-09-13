using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;

namespace Z.Vtuber.Crawler.Services;

public class LiveGuardListFetcher
{
    private readonly IBiliApiClient _bili;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ILogger<LiveGuardListFetcher> _logger;
    private readonly Random _random = Random.Shared;

    public LiveGuardListFetcher(IBiliApiClient bili, BilibiliCrawlerOptions options, ILogger<LiveGuardListFetcher> logger)
    {
        _bili = bili;
        _options = options;
        _logger = logger;
    }

    internal virtual Task<List<LiveGuardEntry>?> FetchAllAsync(string cookieId, string cookie, long mid, long roomId, CancellationToken ct)
    {
        return FetchAllPagesAsync(cookieId, cookie, mid, roomId, ct);
    }

    private async Task<List<LiveGuardEntry>?> FetchAllPagesAsync(string cookieId, string cookie, long mid, long roomId, CancellationToken ct)
    {
        var merged = new Dictionary<long, LiveGuardEntry>();
        var page = 1;

        while (page <= _options.GuardMaxPages)
        {
            GuardInfo info;
            try
            {
                info = await _bili.GetRoomGuardListAsync(cookieId, cookie, roomId, mid, page, ct);
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

            if (page == 1 && info.Top3 is not null)
            {
                foreach (var user in info.Top3)
                {
                    merged.TryAdd(user.Uid, ToEntry(user));
                }
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
}