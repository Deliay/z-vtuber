using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Contracts;

/// <summary>
/// 订阅事件频道约定：订阅创建发生在 api 进程，通过 Redis 广播到 crawler 进程触发即时资料抓取。
/// </summary>
public static class SubscriptionEventChannels
{
    public const string Subscribed = "subscription-subscribed";
}

public sealed record SubscriptionEvent(long Mid, SubscriptionType Type);
