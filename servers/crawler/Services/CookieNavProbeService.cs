using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Services;

/// <summary>
/// 复用 nav 探测 + 帐号信息落库 + 状态机处置，供 CookieProbeJob（周期探测）与 Cookie 首次检出路径共用，
/// 保证「仅称取到可用 Cookie」与「nickname 沉淀」语义一致。
/// </summary>
public sealed class CookieNavProbeService(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    ICookieAccountInfoRepository cookieAccountInfo,
    ICookieInvalidEventPublisher eventPublisher,
    BilibiliCrawlerOptions options,
    ILogger<CookieNavProbeService> logger) : ICookieNavProbeService
{
    public async Task<CookieProbeOutcome> ProbeAsync(CookieRecord record, CancellationToken ct = default)
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

            return CookieProbeOutcome.Success;
        }
        catch (CrawlBackoffActiveException ex)
        {
            logger.LogInformation(ex, "Cookie probe skipped for cookie (id={Id}): backoff active.", record.Id);
            return CookieProbeOutcome.Backoff;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RiskControlException ex)
        {
            await cookiePool.MarkCooldownAsync(record.Cookie, TimeSpan.FromMinutes(options.CooldownMinutes), ct);
            logger.LogWarning(ex, "Cookie (id={Id}) marked cooldown for {Minutes} minutes due to risk control.", record.Id, options.CooldownMinutes);
            return CookieProbeOutcome.Cooldown;
        }
        catch (Exception ex)
        {
            var wasOffline = await cookiePool.MarkOfflineAsync(record.Cookie, ct);
            logger.LogWarning(ex, "Cookie (id={Id}) marked offline due to probe failure.", record.Id);

            // 恰好一次发布：仅当状态迁移成功（Active→Offline）才发布失效事件
            if (wasOffline)
            {
                try
                {
                    await eventPublisher.PublishAsync(record.Id.ToString(), "nav_probe_failed", ct);
                }
                catch (Exception publishEx)
                {
                    logger.LogWarning(publishEx, "Failed to publish cookie invalid event for cookie (id={Id}); continuing.", record.Id);
                }
            }

            return CookieProbeOutcome.Offline;
        }
    }
}
