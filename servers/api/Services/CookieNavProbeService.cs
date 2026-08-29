using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public enum CookieProbeOutcome
{
    /// <summary>nav 探测成功（nav.Mid &gt; 0），Cookie 可用</summary>
    Success,

    /// <summary>风控，已标记 cooldown</summary>
    Cooldown,

    /// <summary>nav.Mid &lt;= 0 或一般异常，已标记 offline</summary>
    Offline,

    /// <summary>目标处于退避期，跳过且不改状态</summary>
    Backoff,
}

public interface ICookieNavProbeService
{
    /// <summary>
    /// 对单个 Cookie 执行 nav 探测，成功时落库帐号信息（独立 try/catch），并按 CookieProbeJob 状态机处置（离线/冷却/跳过）。
    /// </summary>
    Task<CookieProbeOutcome> ProbeAsync(CookieRecord record, CancellationToken ct = default);
}

/// <summary>
/// 复用 nav 探测 + 帐号信息落库 + 状态机处置，供 CookieProbeJob（周期探测）与 Cookie 首次检出路径共用，
/// 保证「仅称取到可用 Cookie」与「nickname 沉淀」语义一致。
/// </summary>
public sealed class CookieNavProbeService(
    IBiliApiClient bili,
    ICookiePoolService cookiePool,
    ICookieAccountInfoRepository cookieAccountInfo,
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
            await cookiePool.MarkOfflineAsync(record.Cookie, ct);
            logger.LogWarning(ex, "Cookie (id={Id}) marked offline due to probe failure.", record.Id);
            return CookieProbeOutcome.Offline;
        }
    }
}