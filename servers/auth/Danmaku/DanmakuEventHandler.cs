using System.Text.RegularExpressions;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Danmaku;

public interface IDanmakuEventHandler
{
    Task HandleAsync(string roomId, DanmakuEvent evt, CancellationToken ct);
}

/// <summary>
/// 弹幕 token 处理：10 位预过滤 → 同 UID 限流 → HMAC 摘要 → <c>pending→verified</c> Mongo CAS。
/// </summary>
public sealed partial class DanmakuEventHandler(
    LoginRequestRepository repository,
    TokenHasher hasher,
    IAuthRateLimiter rateLimiter,
    IOptions<DanmakuOptions> danmakuOptions,
    IOptions<AuthOptions> authOptions,
    AuditLogger audit,
    TimeProvider clock,
    ILogger<DanmakuEventHandler> logger) : IDanmakuEventHandler
{
    [GeneratedRegex("^[A-Za-z0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public async Task HandleAsync(string roomId, DanmakuEvent evt, CancellationToken ct)
    {
        if (!string.Equals(evt.Type, "danmaku", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!danmakuOptions.Value.RoomIds.Contains(roomId, StringComparer.Ordinal))
        {
            return;
        }

        var sender = DanmakuSenderParser.Parse(evt.Payload);
        if (sender is null)
        {
            return;
        }

        if (!TokenPattern().IsMatch(sender.Text))
        {
            return;
        }

        if (!await rateLimiter.TryDanmakuUidAsync(sender.Uid, ct))
        {
            await audit.LogAsync(
                AuthAuditEvents.RateLimited,
                platform: "bilibili",
                platformUid: sender.Uid,
                sourceRoomId: roomId,
                detail: "danmaku token attempts exceeded",
                ct: ct);
            return;
        }

        var tokenHash = hasher.Hash(sender.Text);
        var updated = await repository.TryMarkVerifiedAsync(
            tokenHash,
            roomId,
            sender,
            clock.GetUtcNow().UtcDateTime,
            authOptions.Value.ConfirmWindowSeconds,
            ct);

        if (updated is null)
        {
            return;
        }

        logger.LogInformation(
            "Danmaku token verified for request {RequestId} in room {RoomId} (uid {Uid}).",
            updated.RequestId, roomId, sender.Uid);

        await audit.LogAsync(
            AuthAuditEvents.DanmakuVerified,
            requestId: updated.RequestId,
            platform: "bilibili",
            platformUid: sender.Uid,
            sourceRoomId: roomId,
            ct: ct);
    }
}
