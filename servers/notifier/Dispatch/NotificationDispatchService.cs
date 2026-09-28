using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Notifier.Milky;

namespace Z.Vtuber.Notifier.Dispatch;

public interface INotificationDispatchService
{
    Task DispatchAsync(CookieInvalidEvent evt, CancellationToken ct = default);
}

public sealed class NotificationDispatchService : INotificationDispatchService
{
    private readonly INotificationSubscriptionRepository _subscriptionRepository;
    private readonly IMilkyApiClient _milkyClient;
    private readonly ILogger<NotificationDispatchService> _logger;

    public NotificationDispatchService(
        INotificationSubscriptionRepository subscriptionRepository,
        IMilkyApiClient milkyClient,
        ILogger<NotificationDispatchService> logger)
    {
        _subscriptionRepository = subscriptionRepository;
        _milkyClient = milkyClient;
        _logger = logger;
    }

    public async Task DispatchAsync(CookieInvalidEvent evt, CancellationToken ct = default)
    {
        var eventType = ResolveEventType(evt.Platform);

        var subscriptions = await _subscriptionRepository.GetByEventTypeAsync(eventType, ct);
        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No subscriptions for {EventType} event; ignoring.", eventType);
            return;
        }

        var message = FormatMessage(evt);

        foreach (var subscription in subscriptions)
        {
            try
            {
                var groupId = long.Parse(subscription.GroupId);
                await _milkyClient.SendGroupMessageAsync(groupId, message, ct);
                _logger.LogInformation("Sent {EventType} notification to group {GroupId}", eventType, subscription.GroupId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification to group {GroupId}; continuing.", subscription.GroupId);
            }
        }
    }

    /// <summary>事件平台映射为通知订阅类型；空/未知按 bilibili 处理（向后兼容）。</summary>
    internal static string ResolveEventType(string? platform)
        => string.Equals(platform, "weibo", StringComparison.OrdinalIgnoreCase)
            ? "weibo_cookie_invalid"
            : "bilibili_cookie_invalid";

    private static string PlatformDisplayName(string? platform)
        => string.Equals(platform, "weibo", StringComparison.OrdinalIgnoreCase) ? "微博" : "bilibili";

    private static string FormatMessage(CookieInvalidEvent evt)
    {
        var accountInfo = evt.Uname is not null && evt.Mid.HasValue
            ? $"{evt.Uname}（ID {evt.Mid.Value}）"
            : $"CookieId {evt.CookieId[..Math.Min(8, evt.CookieId.Length)]}...";

        var platformName = PlatformDisplayName(evt.Platform);

        return $"""
            【Z.Vtuber】{platformName} 帐号失效通知
            账号：{accountInfo}
            时间：{evt.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}
            请及时检查并更换 Cookie。
            """;
    }
}