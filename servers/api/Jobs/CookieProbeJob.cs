using Z.Vtuber.Configuration;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class CookieProbeJob(
    ICookiePoolService cookiePool,
    ICookieNavProbeService probeService,
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
            var outcome = await probeService.ProbeAsync(record, ct);
            if (outcome == CookieProbeOutcome.Cooldown)
            {
                cooldown++;
            }
        }

        if (cooldown == cookies.Count)
        {
            logger.LogWarning("All active cookies are now on cooldown; crawling is paused.");
        }
    }
}