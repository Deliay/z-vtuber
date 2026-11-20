using MongoDB.Driver;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.OAuth;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Auth;

public sealed record CreatedLoginRequest(
    string RequestId,
    string Token,
    string PollToken,
    DateTime ExpiresAt,
    int PollAfterMs,
    string[] RoomIds,
    string PrimaryRoomId,
    int ConfirmWindowSeconds,
    string Instruction);

public sealed record LoginRequestStatusView(
    string Status,
    DateTime ExpiresAt,
    DateTime? ConfirmExpiresAt,
    string? SourceRoomId,
    AuthUserView? User,
    int RetryAfterMs);

public sealed record ConfirmLoginResult(
    string Mode,
    MongoDB.Bson.ObjectId UserId,
    AuthUserView User,
    string? RedirectUrl,
    DateTime? ProviderTicketExpiresAt,
    StandaloneTokens? Tokens);

/// <summary>登录请求创建、状态查询、两段 CAS 确认与取消。</summary>
public sealed class LoginRequestService(
    LoginRequestRepository repository,
    TokenHasher hasher,
    UserService userService,
    ProviderTicketService ticketService,
    StandaloneSessionService standaloneSessionService,
    IOptions<AuthOptions> authOptions,
    IOptions<OAuthProviderOptions> providerOptions,
    IOptions<DanmakuOptions> danmakuOptions,
    AuditLogger audit,
    TimeProvider clock,
    ILogger<LoginRequestService> logger)
{
    public const int DefaultPollAfterMs = 1500;

    public async Task<CreatedLoginRequest> CreateAsync(
        string? clientId,
        string? clientNonce,
        string? returnTo,
        string? clientIp,
        string? userAgent,
        CancellationToken ct = default)
    {
        var options = authOptions.Value;
        if (string.Equals(options.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
        {
            // casdoor 模式必须从站点登录入口带完整 /oauth/authorize returnTo 进入。
            _ = ReturnToValidator.Validate(returnTo, providerOptions.Value);
        }
        else
        {
            returnTo = null;
        }

        if (clientId is { Length: > 128 })
        {
            throw AuthException.Invalid("clientId 过长。");
        }

        if (clientNonce is { Length: > 256 })
        {
            throw AuthException.Invalid("clientNonce 过长。");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var requestId = TokenGenerator.CreateRequestId();
            var token = TokenGenerator.CreateDanmakuToken();
            var pollToken = TokenGenerator.CreatePollToken();
            var record = new AuthLoginRequestRecord
            {
                RequestId = requestId,
                TokenHash = hasher.Hash(token),
                PollTokenHash = hasher.Hash(pollToken),
                ClientNonceHash = string.IsNullOrWhiteSpace(clientNonce) ? null : hasher.Hash(clientNonce),
                ClientId = clientId,
                ReturnTo = returnTo,
                Status = LoginStatus.Pending,
                ExpiresAt = now.AddSeconds(options.LoginTokenTtlSeconds),
                CreatedAt = now,
                ClientIp = Truncate(clientIp, 64),
                UserAgent = Truncate(userAgent, 256)
            };

            try
            {
                await repository.InsertAsync(record, ct);
                await audit.LogAsync(
                    AuthAuditEvents.LoginRequestCreated,
                    requestId: requestId,
                    platform: "bilibili",
                    clientIp: clientIp,
                    userAgent: userAgent,
                    ct: ct);

                var rooms = danmakuOptions.Value.RoomIds;
                return new CreatedLoginRequest(
                    requestId,
                    token,
                    pollToken,
                    record.ExpiresAt,
                    DefaultPollAfterMs,
                    rooms,
                    rooms[0],
                    options.ConfirmWindowSeconds,
                    rooms.Length > 1
                        ? $"请在本平台配置的任一直播间发送上方 token（{string.Join("、", rooms)}）。"
                        : $"请在直播间 {rooms[0]} 发送上方 token。");
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                logger.LogInformation("Danmaku token collision on attempt {Attempt}; retrying.", attempt + 1);
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique danmaku token after 5 attempts.");
    }

    public async Task<(AuthLoginRequestRecord Record, LoginRequestStatusView View)> GetAsync(
        string requestId, string? pollToken, CancellationToken ct = default)
    {
        var record = await RequireRequestAsync(requestId, pollToken, ct);
        var status = EffectiveStatus(record, clock.GetUtcNow().UtcDateTime, ConfirmTimeout());
        var user = BuildUserView(record);
        return (record, new LoginRequestStatusView(
            status,
            record.ExpiresAt,
            record.ConfirmExpiresAt,
            record.SourceRoomId,
            status is LoginStatus.Verified or LoginStatus.Confirming or LoginStatus.Consumed ? user : null,
            DefaultPollAfterMs));
    }

    public async Task<ConfirmLoginResult> ConfirmAsync(
        string requestId, string? pollToken, string? clientIp, string? userAgent, CancellationToken ct = default)
    {
        var record = await RequireRequestAsync(requestId, pollToken, ct);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var status = EffectiveStatus(record, nowUtc, ConfirmTimeout());
        switch (status)
        {
            case LoginStatus.Consumed:
                throw AuthException.AlreadyConsumed();
            case LoginStatus.Expired:
            case LoginStatus.Cancelled:
                throw AuthException.Expired();
            case LoginStatus.Failed:
                throw AuthException.Failed();
            case LoginStatus.Confirming:
                throw AuthException.ConfirmRace();
            case LoginStatus.Pending:
                throw AuthException.NotVerified();
        }

        if (record.SenderUid is null or <= 0)
        {
            throw AuthException.NotVerified();
        }

        // confirming 已超过操作超时但确认窗口仍有效：先回退为 verified，允许重新确认。
        if (string.Equals(record.Status, LoginStatus.Confirming, StringComparison.Ordinal) &&
            !await repository.TryRevertStaleConfirmAsync(requestId, nowUtc - ConfirmTimeout(), ct))
        {
            throw AuthException.ConfirmRace();
        }

        var nonce = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow().UtcDateTime;
        var confirming = await repository.TryStartConfirmAsync(requestId, nonce, now, ct);
        if (confirming is null)
        {
            throw AuthException.ConfirmRace();
        }

        ProviderTicketResult? ticket = null;
        StandaloneTokens? tokens = null;
        try
        {
            var upsert = await userService.UpsertBilibiliUserAsync(
                record.SenderUid.Value,
                record.SenderName ?? $"UID:{record.SenderUid}",
                record.SenderAvatar,
                record.SourceRoomId,
                clientIp,
                ct);
            if (upsert.Disabled)
            {
                await repository.RevertToVerifiedAsync(requestId, nonce, ct);
                throw AuthException.UserDisabled();
            }

            var userView = UserService.ToView(upsert.User);
            if (string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(record.ReturnTo))
                {
                    await repository.TryMarkFailedAsync(
                        requestId, nonce, AuthErrorCodes.ProviderReturnToInvalid, clock.GetUtcNow().UtcDateTime, ct);
                    throw AuthException.ProviderReturnToInvalid();
                }

                try
                {
                    ticket = await ticketService.CreateAsync(upsert.User.Id, record.ReturnTo, ct);
                }
                catch (AuthException ex) when (ex.Code == AuthErrorCodes.InvalidRequest)
                {
                    await repository.TryMarkFailedAsync(
                        requestId, nonce, AuthErrorCodes.ProviderReturnToInvalid, clock.GetUtcNow().UtcDateTime, ct);
                    throw AuthException.ProviderReturnToInvalid();
                }
            }
            else
            {
                tokens = await standaloneSessionService.IssueAsync(upsert.User.Id, clientIp, userAgent, ct);
            }

            var consumed = await repository.TryMarkConsumedAsync(
                requestId, nonce, clock.GetUtcNow().UtcDateTime, ct);
            if (!consumed)
            {
                throw AuthException.ConfirmRace();
            }

            await audit.LogAsync(
                AuthAuditEvents.ConfirmSucceeded,
                requestId: requestId,
                platform: "bilibili",
                platformUid: record.SenderUid,
                sourceRoomId: record.SourceRoomId,
                clientIp: clientIp,
                ct: ct);

            return new ConfirmLoginResult(
                authOptions.Value.Mode,
                upsert.User.Id,
                userView,
                ticket?.RedirectUrl,
                ticket?.ExpiresAt,
                tokens);
        }
        catch (AuthException ex)
        {
            if (ex.Code != AuthErrorCodes.UserDisabled &&
                ex.Code != AuthErrorCodes.ProviderReturnToInvalid)
            {
                await repository.RevertToVerifiedAsync(requestId, nonce, CancellationToken.None);
            }

            await RevokeIssuedCredentialsAsync(requestId, nonce, ticket, tokens, CancellationToken.None);
            await audit.LogAsync(
                AuthAuditEvents.ConfirmFailed,
                requestId: requestId,
                platform: "bilibili",
                platformUid: record.SenderUid,
                sourceRoomId: record.SourceRoomId,
                clientIp: clientIp,
                detail: ex.Code,
                ct: ct);
            throw;
        }
        catch
        {
            await repository.RevertToVerifiedAsync(requestId, nonce, CancellationToken.None);
            await RevokeIssuedCredentialsAsync(requestId, nonce, ticket, tokens, CancellationToken.None);
            await audit.LogAsync(
                AuthAuditEvents.ConfirmFailed,
                requestId: requestId,
                platform: "bilibili",
                platformUid: record.SenderUid,
                sourceRoomId: record.SourceRoomId,
                clientIp: clientIp,
                ct: ct);
            throw;
        }
    }

    /// <summary>失败路径对称撤销已签发的 Provider Ticket / standalone access+refresh，避免无主凭证。</summary>
    private async Task RevokeIssuedCredentialsAsync(
        string requestId,
        string nonce,
        ProviderTicketResult? ticket,
        StandaloneTokens? tokens,
        CancellationToken ct)
    {
        try
        {
            if (ticket is not null)
            {
                await ticketService.RevokeAsync(ticket.Ticket, ct);
            }

            if (tokens is not null)
            {
                await standaloneSessionService.LogoutAsync(tokens.AccessToken, tokens.RefreshToken, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to revoke issued credentials for login request {RequestId}.", requestId);
        }
    }

    public async Task CancelAsync(string requestId, string? pollToken, string? clientIp, CancellationToken ct = default)
    {
        var record = await RequireRequestAsync(requestId, pollToken, ct);
        var status = EffectiveStatus(record, clock.GetUtcNow().UtcDateTime, ConfirmTimeout());
        if (string.Equals(status, LoginStatus.Consumed, StringComparison.Ordinal))
        {
            throw AuthException.AlreadyConsumed();
        }

        if (string.Equals(status, LoginStatus.Cancelled, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(status, LoginStatus.Expired, StringComparison.Ordinal))
        {
            throw AuthException.Expired();
        }

        if (string.Equals(status, LoginStatus.Confirming, StringComparison.Ordinal))
        {
            throw AuthException.ConfirmRace();
        }

        if (string.Equals(status, LoginStatus.Failed, StringComparison.Ordinal))
        {
            throw AuthException.Failed();
        }

        var cancelled = await repository.TryCancelAsync(
            requestId, clock.GetUtcNow().UtcDateTime, ct);
        if (cancelled)
        {
            await audit.LogAsync(
                AuthAuditEvents.Cancelled,
                requestId: requestId,
                platform: "bilibili",
                clientIp: clientIp,
                ct: ct);
        }
    }

    public async Task<AuthLoginRequestRecord> RequireRequestAsync(
        string requestId, string? pollToken, CancellationToken ct)
    {
        var record = await repository.FindByRequestIdAsync(requestId, ct);
        // 未知 requestId 与错误 pollToken 统一返回 404，避免通过状态码探测请求是否存在。
        if (record is null || !hasher.Verify(pollToken ?? string.Empty, record.PollTokenHash))
        {
            throw AuthException.NotFound();
        }

        return record;
    }

    private TimeSpan ConfirmTimeout() =>
        TimeSpan.FromSeconds(Math.Max(1, authOptions.Value.ConfirmOperationTimeoutSeconds));

    public static string EffectiveStatus(AuthLoginRequestRecord record)
    {
        var now = DateTime.UtcNow;
        return EffectiveStatus(record, now);
    }

    public static string EffectiveStatus(AuthLoginRequestRecord record, DateTime nowUtc) =>
        EffectiveStatus(record, nowUtc, DefaultConfirmTimeout);

    public static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromSeconds(30);

    public static string EffectiveStatus(AuthLoginRequestRecord record, DateTime nowUtc, TimeSpan confirmTimeout)
    {
        if (string.Equals(record.Status, LoginStatus.Pending, StringComparison.Ordinal) &&
            record.ExpiresAt <= nowUtc)
        {
            return LoginStatus.Expired;
        }

        if (string.Equals(record.Status, LoginStatus.Verified, StringComparison.Ordinal) &&
            record.ConfirmExpiresAt is not null &&
            record.ConfirmExpiresAt <= nowUtc)
        {
            return LoginStatus.Expired;
        }

        if (string.Equals(record.Status, LoginStatus.Confirming, StringComparison.Ordinal))
        {
            if (record.ConfirmExpiresAt is not null && record.ConfirmExpiresAt <= nowUtc)
            {
                return LoginStatus.Expired;
            }

            if (record.ConfirmStartedAt is not null && record.ConfirmStartedAt <= nowUtc - confirmTimeout)
            {
                return LoginStatus.Verified;
            }
        }

        return record.Status;
    }

    private static AuthUserView? BuildUserView(AuthLoginRequestRecord record) =>
        record.SenderUid is > 0
            ? new AuthUserView(
                "bilibili",
                record.SenderUid.Value,
                string.IsNullOrWhiteSpace(record.SenderName) ? $"UID:{record.SenderUid}" : record.SenderName,
                record.SenderAvatar)
            : null;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}

/// <summary>每 30 秒把超时记录 CAS 迁移为 <c>expired</c>，并把卡死的 <c>confirming</c> 回退为 <c>verified</c>。</summary>
public sealed class LoginRequestScanner(
    LoginRequestRepository repository,
    IOptions<AuthOptions> authOptions,
    AuditLogger audit,
    TimeProvider clock,
    ILogger<LoginRequestScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Login request scanner failed; retrying next tick.");
            }
        }
    }

    public async Task<int> ScanOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var confirmTimeout = TimeSpan.FromSeconds(Math.Max(1, authOptions.Value.ConfirmOperationTimeoutSeconds));
        var expired = await repository.FindExpiredAsync(now, confirmTimeout, 200, ct);
        foreach (var record in expired)
        {
            if (!await repository.TryExpireAsync(record, now, ct))
            {
                continue;
            }

            await audit.LogAsync(
                AuthAuditEvents.Expired,
                requestId: record.RequestId,
                platform: record.Platform,
                platformUid: record.SenderUid,
                sourceRoomId: record.SourceRoomId,
                detail: string.Equals(record.Status, LoginStatus.Confirming, StringComparison.Ordinal)
                    ? "confirm_timeout_reverted"
                    : "expired",
                ct: ct);
        }

        if (expired.Count > 0)
        {
            logger.LogInformation("Processed {Count} timed-out login requests.", expired.Count);
        }

        return expired.Count;
    }
}
