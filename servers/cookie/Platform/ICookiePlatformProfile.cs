namespace Z.Vtuber.Cookie.Platform;

/// <summary>
/// 平台差异抽象：封装登录页、二维码定位、登录态检测信号、cookie 提取域、锁空间与账号身份解析。
/// 浏览器交互由 <see cref="Browser.IBrowserSessionManager"/> 按此配置驱动。
/// </summary>
public interface ICookiePlatformProfile
{
    /// <summary>平台标识（"bilibili" / "weibo"）。</summary>
    string Platform { get; }

    /// <summary>登录页 URL。</summary>
    string LoginUrl { get; }

    /// <summary>二维码元素定位器（截取该元素作为二维码；为空则整页截图兜底）。</summary>
    string QrCodeSelector { get; }

    /// <summary>保活刷新着陆页。</summary>
    string HomeUrl { get; }

    /// <summary>context.CookiesAsync 目标域。</summary>
    string CookieDomainUrl { get; }

    /// <summary>被动观察的登录态响应 URL 片段（如 bilibili nav）；使用主动探测时为空。</summary>
    string? LoginStateObservedUrlFragment { get; }

    /// <summary>主动探测登录态的 JSON 接口 URL（如微博 api/config）；使用被动观察时为空。</summary>
    string? LoginStateProbeUrl { get; }

    /// <summary>分布式锁 key 平台段（bilibili 为空以保持既有锁 key 不变）。</summary>
    string LockKeyPrefix { get; }

    /// <summary>登录 cookie 必须包含的关键 cookie 名（缺失时提取仅告警，不抛错）。</summary>
    IReadOnlyList<string> RequiredCookieNames { get; }

    /// <summary>判断响应体是否表示已登录。</summary>
    bool IsLoggedIn(string responseBody);

    /// <summary>从登录态响应体解析账号身份（uid/昵称）。</summary>
    IdentityExtractionResult ExtractIdentity(string responseBody);
}

public record IdentityExtractionResult
{
    public long? Mid { get; init; }
    public string? Uname { get; init; }
}
