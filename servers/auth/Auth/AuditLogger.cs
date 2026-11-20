using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Auth;

public static class AuthAuditEvents
{
    public const string LoginRequestCreated = "login_request_created";
    public const string DanmakuVerified = "danmaku_verified";
    public const string ConfirmSucceeded = "confirm_succeeded";
    public const string ConfirmFailed = "confirm_failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string RateLimited = "rate_limited";
    public const string OAuthAuthorize = "oauth_authorize";
    public const string OAuthTokenIssued = "oauth_token_issued";
    public const string OAuthUserInfo = "oauth_userinfo";
    public const string Logout = "logout";
}

/// <summary>best-effort 审计写入；Detail 只允许脱敏信息，禁止 token/ticket/code/Authorization。</summary>
public sealed class AuditLogger(
    AuditRepository repository,
    TimeProvider clock,
    ILogger<AuditLogger> logger)
{
    public async Task LogAsync(
        string eventName,
        string? requestId = null,
        string? sessionId = null,
        string? platform = null,
        long? platformUid = null,
        string? sourceRoomId = null,
        string? clientIp = null,
        string? userAgent = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await repository.InsertAsync(new AuthAuditLogRecord
            {
                Event = eventName,
                RequestId = requestId,
                SessionId = sessionId,
                Platform = platform,
                PlatformUid = platformUid,
                SourceRoomId = sourceRoomId,
                ClientIp = Truncate(clientIp, 64),
                UserAgent = Truncate(userAgent, 256),
                OccurredAt = clock.GetUtcNow().UtcDateTime,
                Detail = Truncate(detail, 200)
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write auth audit event {Event}.", eventName);
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
