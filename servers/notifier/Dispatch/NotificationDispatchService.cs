using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Notifier.Satori;

namespace Z.Vtuber.Notifier.Dispatch;

public interface INotificationDispatchService
{
    Task DispatchAsync(CookieInvalidEvent evt, CancellationToken ct = default);
}

public sealed class NotificationDispatchService : INotificationDispatchService
{
    private readonly INotificationSubscriptionRepository _subscriptionRepository;
    private readonly ISatoriApiClient _satoriClient;
    private readonly ILogger<NotificationDispatchService> _logger;

    public NotificationDispatchService(
        INotificationSubscriptionRepository subscriptionRepository,
        ISatoriApiClient satoriClient,
        ILogger<NotificationDispatchService> logger)
    {
        _subscriptionRepository = subscriptionRepository;
        _satoriClient = satoriClient;
        _logger = logger;
    }

    public async Task DispatchAsync(CookieInvalidEvent evt, CancellationToken ct = default)
    {
        var subscriptions = await _subscriptionRepository.GetByEventTypeAsync("cookie_invalid", ct);
        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No subscriptions for cookie_invalid event; ignoring.");
            return;
        }

        var message = FormatMessage(evt);

        foreach (var subscription in subscriptions)
        {
            try
            {
                await _satoriClient.SendMessageAsync(subscription.ChannelId, message, ct);
                _logger.LogInformation("Sent cookie invalid notification to channel {ChannelId}", subscription.ChannelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification to channel {ChannelId}; continuing.", subscription.ChannelId);
            }
        }
    }

    private static string FormatMessage(CookieInvalidEvent evt)
    {
        var accountInfo = evt.Uname is not null && evt.Mid.HasValue
            ? $"{evt.Uname}（MID {evt.Mid.Value}）"
            : $"CookieId {evt.CookieId[..Math.Min(8, evt.CookieId.Length)]}...";

        return $"""
            【Z.Vtuber】Cookie 失效通知
            账号：{accountInfo}
            时间：{evt.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}
            请及时检查并更换 Cookie。
            """;
    }
}
