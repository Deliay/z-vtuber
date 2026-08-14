using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class CookieProbeJob(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    BilibiliCrawlerOptions options,
    ILogger<CookieProbeJob> logger) : PeriodicJobBase(logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(options.CookieProbeIntervalMinutes);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var cookies = await cookiePool.GetActiveAsync(ct);
        if (cookies.Count == 0)
        {
            logger.LogWarning("No active cookies; crawling paused until a valid cookie is added.");
            return;
        }

        var offline = 0;
        foreach (var record in cookies)
        {
            try
            {
                var nav = await bili.GetNavInfoV2Async(record.Cookie, ct);
                if (nav.Mid <= 0)
                {
                    throw new InvalidOperationException("nav returned mid=0");
                }
            }
            catch (Exception ex)
            {
                await cookiePool.MarkOfflineAsync(record.Cookie, ct);
                offline++;
                logger.LogWarning(ex, "Cookie (id={Id}) marked offline.", record.Id);
            }
        }

        if (offline == cookies.Count)
        {
            logger.LogError("All cookies are offline; crawling is paused.");
        }
    }
}
