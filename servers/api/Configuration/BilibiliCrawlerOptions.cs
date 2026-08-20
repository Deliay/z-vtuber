namespace Z.Vtuber.Configuration;

public enum BiliFetchMode
{
    Direct,
    Browser,
    Auto
}

public sealed class BilibiliCrawlerOptions
{
    public const string SectionName = "BilibiliCrawler";

    public int StatusIntervalSeconds { get; set; } = 15;
    public int SpaceTimelineIntervalSeconds { get; set; } = 30;
    public int AccountInfoIntervalHours { get; set; } = 168;
    public int CookieProbeIntervalMinutes { get; set; } = 5;
    public int HttpTimeoutSeconds { get; set; } = 10;

    public BiliFetchMode FetchMode { get; set; } = BiliFetchMode.Auto;
    public int BackoffBaseSeconds { get; set; } = 30;
    public int BackoffMaxMinutes { get; set; } = 15;
    public int CooldownMinutes { get; set; } = 30;
    public int RiskControlCooldownThreshold { get; set; } = 5;
    public int BrowserMinDelaySeconds { get; set; } = 2;
    public int BrowserMaxDelaySeconds { get; set; } = 5;
    public string BrowserUserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    public int ChannelRecoverSuccessCount { get; set; } = 3;
}
