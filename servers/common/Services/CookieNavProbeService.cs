using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Services;

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