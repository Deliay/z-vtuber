namespace Z.Vtuber.Auth.Configuration;

/// <summary>
/// 账号认证服务主配置（节 <c>Auth</c>）。
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>运行模式：<c>casdoor</c>（默认，Casdoor Custom OAuth Provider）或 <c>standalone</c>（本地降级）。</summary>
    public string Mode { get; set; } = AuthModes.Casdoor;

    /// <summary>本服务对外基址（用于 Cookie Secure 判定与审计），必须是绝对地址。</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8087";

    /// <summary>验证/注册页地址；默认本服务相对路径，也允许独立登录服务的绝对地址。</summary>
    public string AccountCenterUrl { get; set; } = "/connect/bilibili";

    /// <summary>token/poll token/ticket/授权码/token 的 HMAC pepper，至少 32 字符。</summary>
    public string TokenPepper { get; set; } = string.Empty;

    public int LoginTokenTtlSeconds { get; set; } = 300;

    public int ConfirmWindowSeconds { get; set; } = 120;

    /// <summary>confirming 状态的最长停留时间；超时由扫描器回退为 verified，避免记录卡死。</summary>
    public int ConfirmOperationTimeoutSeconds { get; set; } = 30;

    public int ProviderTicketSeconds { get; set; } = 60;

    public int ProviderSessionDays { get; set; } = 30;

    /// <summary>Redis 不可用时是否所有实例都订阅弹幕（依赖 Mongo CAS 去重）。</summary>
    public bool AllowDegradedRedis { get; set; }

    /// <summary>是否信任反向代理的 X-Forwarded-* 头。</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>预留管理 API Key；本期不开放管理端点。</summary>
    public string? AdminApiKey { get; set; }

    /// <summary>账号中心 API 的 CORS 白名单；为空表示不启用跨域。</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public AuthRateLimitOptions RateLimit { get; set; } = new();

    public StandaloneOptions Standalone { get; set; } = new();
}

public static class AuthModes
{
    public const string Casdoor = "casdoor";
    public const string Standalone = "standalone";
}

public sealed class AuthRateLimitOptions
{
    public int TokenPerMinutePerIp { get; set; } = 10;

    public int TokenPerDayPerIp { get; set; } = 100;

    public int TokenPerHourPerClient { get; set; } = 100;

    public int DanmakuPerMinutePerUid { get; set; } = 10;

    public int OAuthTokenPerMinutePerClient { get; set; } = 60;
}

/// <summary>standalone 模式配置（节 <c>Auth:Standalone</c>）。</summary>
public sealed class StandaloneOptions
{
    public int AccessTokenSeconds { get; set; } = 900;

    public int RefreshTokenDays { get; set; } = 30;
}
