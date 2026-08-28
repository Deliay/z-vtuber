using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class CookieProbeJob(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    ICookieAccountInfoRepository cookieAccountInfo,
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

                try
                {
                    await cookieAccountInfo.UpsertAsync(new CookieAccountInfoRecord
                    {
                        CookieId = record.Id,
                        Mid = nav.Mid,
                        Uname = nav.Name,
                        FetchedAt = DateTime.UtcNow,
                    }, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Cookie account info persist failed for cookie (id={Id}); probe outcome unchanged.", record.Id);
                }
            }
            catch (CrawlBackoffActiveException ex)
            {
                logger.LogInformation(ex, "Cookie probe skipped for cookie (id={Id}): backoff active.", record.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RiskControlException ex)
            {
                await cookiePool.MarkCooldownAsync(record.Cookie, TimeSpan.FromMinutes(options.CooldownMinutes), ct);
                cooldown++;
                logger.LogWarning(ex, "Cookie (id={Id}) marked cooldown for {Minutes} minutes due to risk control.", record.Id, options.CooldownMinutes);
            }
            catch (Exception ex)
            {
                await cookiePool.MarkOfflineAsync(record.Cookie, ct);
                logger.LogWarning(ex, "Cookie (id={Id}) marked offline due to probe failure.", record.Id);
            }
        }

        if (cooldown == cookies.Count)
        {
            logger.LogWarning("All active cookies are now on cooldown; crawling is paused.");
        }
    }
}
