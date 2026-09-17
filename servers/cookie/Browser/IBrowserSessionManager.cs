namespace Z.Vtuber.Cookie.Browser;

/// <summary>
/// 浏览器 session 抽象。实现采用「按需惰性打开 + 用后空闲延时关闭」：
/// <see cref="CreateLoginSessionAsync"/>（扫码登录）期间 context/page 保持打开；
/// <see cref="ExtractCookiesAsync"/> / <see cref="RefreshSessionAsync"/> 在 context 不存在时按需冷启动，
/// 使用完毕后经 <c>PageIdleCloseDelaySeconds</c> 空闲延时自动关闭；期间再次使用则顺延。
/// </summary>
public interface IBrowserSessionManager
{
    Task<LoginSessionResult> CreateLoginSessionAsync(string accountId, CancellationToken ct = default);
    Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>提取该帐号 cookie；context 不存在时按需打开，完成后启动/重置空闲关闭计时。</summary>
    Task<string> ExtractCookiesAsync(string accountId, CancellationToken ct = default);

    /// <summary>删除该帐号 session：取消未触发的空闲计时并立即关闭 context。</summary>
    Task DeleteSessionAsync(string accountId, CancellationToken ct = default);

    /// <summary>刷新该帐号登录态；context 不存在时按需打开，完成后启动/重置空闲关闭计时。</summary>
    Task RefreshSessionAsync(string accountId, CancellationToken ct = default);
}
