namespace Z.Vtuber.Auth.Configuration;

/// <summary>弹幕事件源配置（节 <c>Auth:Danmaku</c>）。</summary>
public sealed class DanmakuOptions
{
    public const string SectionName = "Auth:Danmaku";

    /// <summary>要订阅的直播间列表；roomId 全程按字符串处理。</summary>
    public string[] RoomIds { get; set; } = ["30066"];

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public string SubscribePath { get; set; } = "/api/b/live/{roomId}/event/subscribe?type=danmaku";

    public string RangePath { get; set; } = "/api/b/live/{roomId}/event/range";

    public int BackfillWindowSeconds { get; set; } = 600;

    public int NotReadyGraceSeconds { get; set; } = 30;

    public int ReadTimeoutSeconds { get; set; } = 60;

    public int MaxBackfillPages { get; set; } = 10;

    public int LeaderLockTtlSeconds { get; set; } = 15;

    public int ReconnectMaxSeconds { get; set; } = 30;

    public string BuildSubscribePath(string roomId) => SubscribePath.Replace("{roomId}", roomId, StringComparison.Ordinal);

    public string BuildRangePath(string roomId) => RangePath.Replace("{roomId}", roomId, StringComparison.Ordinal);
}
