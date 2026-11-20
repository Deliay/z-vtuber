using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Observability;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.OAuth;

[ApiController]
public sealed class OAuthUserInfoController(
    IOptions<AuthOptions> authOptions,
    ProviderTokenService tokens,
    UserRepository users,
    AuditLogger audit,
    AuthMetrics metrics) : ControllerBase
{
    [HttpGet("/oauth/userinfo")]
    public async Task<IActionResult> UserInfo(CancellationToken ct)
    {
        if (!string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return UnauthorizedResponse();
        }

        var record = await tokens.ValidateAccessAsync(header["Bearer ".Length..].Trim(), ct);
        if (record is null)
        {
            metrics.OAuthFailure();
            await AuditFailureAsync("invalid_token", ct);
            return UnauthorizedResponse();
        }

        var user = await users.FindByIdAsync(record.UserId, ct);
        if (user is null || !string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal))
        {
            metrics.OAuthFailure();
            await AuditFailureAsync("user_inactive", ct);
            return UnauthorizedResponse();
        }

        await audit.LogAsync(
            AuthAuditEvents.OAuthUserInfo,
            platform: "bilibili",
            platformUid: user.PlatformUid,
            clientIp: ClientIp(),
            userAgent: Request.Headers.UserAgent.ToString(),
            ct: ct);

        return Ok(new
        {
            sub = user.ProviderSubject,
            preferred_username = user.ProviderSubject,
            name = user.DisplayName,
            picture = user.AvatarUrl,
            bilibili_uid = user.PlatformUid.ToString(),
            bilibili_room_id = user.LastSourceRoomId
        });
    }

    private IActionResult UnauthorizedResponse()
    {
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return StatusCode(StatusCodes.Status401Unauthorized, new
        {
            error = "invalid_token",
            error_description = "The access token is invalid or expired."
        });
    }

    private Task AuditFailureAsync(string detail, CancellationToken ct) =>
        audit.LogAsync(
            AuthAuditEvents.OAuthUserInfo,
            clientIp: ClientIp(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detail: detail,
            ct: ct);

    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

[ApiController]
public sealed class OAuthLogoutController(
    IOptions<AuthOptions> authOptions,
    IOptions<OAuthProviderOptions> providerOptions,
    ProviderTokenService tokens,
    ProviderSessionService sessions,
    AuditLogger audit,
    ILogger<OAuthLogoutController> logger) : ControllerBase
{
    [HttpPost("/oauth/logout")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Logout(
        [FromForm] string? token,
        [FromForm] string? refresh_token,
        [FromForm] string? client_id,
        [FromForm] string? client_secret,
        CancellationToken ct)
    {
        if (!string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var client = AuthenticateClient(client_id, client_secret);
        if (client is null)
        {
            return UnauthorizedClient();
        }

        // 先反查用户再撤销 token，确保 Provider Session 一并失效。
        var userId = await tokens.FindUserIdAsync(token, refresh_token, ct);
        await tokens.RevokeAsync(token, refresh_token, ct);

        var cookieToken = Request.Cookies[ProviderSessionService.CookieName];
        await sessions.RevokeAsync(cookieToken, ct);
        if (userId is not null)
        {
            await sessions.RevokeAllForUserAsync(userId.Value, ct);
        }

        ProviderSessionCookie.Delete(Response, authOptions.Value.PublicBaseUrl);
        await audit.LogAsync(
            AuthAuditEvents.Logout,
            clientIp: ClientIp(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detail: "oauth_logout",
            ct: ct);
        logger.LogInformation("OAuth logout processed for client {ClientId}.", client);
        return Ok(new { });
    }

    private string? AuthenticateClient(string? formClientId, string? formClientSecret)
    {
        var provider = providerOptions.Value;
        string? providedId = formClientId;
        string? providedSecret = formClientSecret;
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
                var separator = decoded.IndexOf(':');
                if (separator >= 0)
                {
                    providedId = decoded[..separator];
                    providedSecret = decoded[(separator + 1)..];
                }
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // RFC 7009：client 认证为必填，不允许匿名注销。
        if (string.IsNullOrEmpty(providedId) || string.IsNullOrEmpty(providedSecret) ||
            !TokenHasher.FixedTimeEquals(providedId, provider.ClientId) ||
            !TokenHasher.FixedTimeEquals(providedSecret, provider.ClientSecret))
        {
            return null;
        }

        return provider.ClientId;
    }

    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private IActionResult UnauthorizedClient()
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"oauth\"";
        return StatusCode(StatusCodes.Status401Unauthorized, new
        {
            error = "invalid_client",
            error_description = "Client authentication failed."
        });
    }
}
