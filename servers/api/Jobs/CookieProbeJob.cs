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

    internal override async Task RunOnceAsync(CancellationToken ct)
    {
        var cookies = await cookiePool.GetActiveAsync(ct);
        if (cookies.Count == 0)
        {
            logger.LogWarning("No active cookies (all cooldown or offline); crawling paused until a valid cookie is added.");
            return;
        }

        var cooldown = 0;
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
            catch (CrawlBackoffActiveException ex)
            {
                logger.LogInformation(ex, "Cookie probe skipped for cookie (id={Id}): backoff active.", record.Id);
            }
            catch (Exception ex)
            {
                await cookiePool.MarkCooldownAsync(record.Cookie, TimeSpan.FromMinutes(options.CooldownMinutes), ct);
                cooldown++;
                logger.LogWarning(ex, "Cookie (id={Id}) marked cooldown for {Minutes} minutes.", record.Id, options.CooldownMinutes);
            }
        }

        if (cooldown == cookies.Count)
        {
            logger.LogWarning("All active cookies are now on cooldown; crawling is paused.");
        }
    }
}
