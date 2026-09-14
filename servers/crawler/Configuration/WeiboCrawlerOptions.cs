namespace Z.Vtuber.Crawler.Configuration;

public sealed class WeiboCrawlerOptions
{
    public const string SectionName = "WeiboCrawler";

    public int TimelineIntervalSeconds { get; set; } = 10;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public int CookieRefreshIntervalMinutes { get; set; } = 60;
    public int MaxPagesPerCrawl { get; set; } = 5;
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";

    public int BackoffBaseSeconds { get; set; } = 30;
    public int BackoffMaxMinutes { get; set; } = 15;
    public int CooldownMinutes { get; set; } = 10;
    public int RiskControlCooldownThreshold { get; set; } = 5;

    /// <summary>是否优先使用 cookie 服务领取的登录 cookie；false 时纯匿名访客模式。</summary>
    public bool UseLoginCookie { get; set; } = true;

    /// <summary>登录 cookie 命中风控后是否阻塞宣告并重试一次。</summary>
    public bool LoginCookieRetryOnce { get; set; } = true;
}
