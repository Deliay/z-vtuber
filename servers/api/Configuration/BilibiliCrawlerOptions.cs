namespace Z.Vtuber.Configuration;

public sealed class BilibiliCrawlerOptions
{
    public const string SectionName = "BilibiliCrawler";

    public int StatusIntervalSeconds { get; set; } = 15;
    public int SpaceTimelineIntervalSeconds { get; set; } = 30;
    public int AccountInfoIntervalHours { get; set; } = 24;
    public int CookieProbeIntervalMinutes { get; set; } = 5;
    public int HttpTimeoutSeconds { get; set; } = 10;
}
