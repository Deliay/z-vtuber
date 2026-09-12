namespace Z.Vtuber.Cookie.Configuration;

public class CookieServiceOptions
{
    public string BrowserSessionDataDir { get; set; } = "/data/browser-sessions";
    public int QrCodeExpiresSeconds { get; set; } = 180;
    public int RefreshIntervalMinutes { get; set; } = 10;
    public int RefreshNavTimeoutSeconds { get; set; } = 30;
    public int MaxConsecutiveRefreshFailures { get; set; } = 3;
    public int ReportInvalidTimeoutSeconds { get; set; } = 60;
    public int MaxPendingLoginSessions { get; set; } = 3;
    public int RefreshLockTtlSeconds { get; set; } = 90;
    public int LockAcquireWaitMs { get; set; } = 5000;
    public int LockRetryIntervalMs { get; set; } = 100;
}