using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Contracts;
using Z.Vtuber.Auth.OAuth;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class MeController(
    ProviderSessionService providerSessions,
    StandaloneSessionService standaloneSessions,
    ProviderTokenService providerTokens,
    UserRepository users,
    IOptions<AuthOptions> authOptions,
    IOptions<CasdoorOptions> casdoorOptions) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var resolved = await ResolveAsync(ct)
            ?? throw AuthException.Unauthorized();
        return Ok(ToResponse(resolved));
    }

    internal async Task<AuthUserRecord?> ResolveAsync(CancellationToken ct)
    {
        var cookie = Request.Cookies[ProviderSessionService.CookieName];
        var fromCookie = await providerSessions.ResolveAsync(cookie, ct);
        if (fromCookie is not null)
        {
            // 滑动续期：每次命中会话都同步浏览器 Cookie，与 DB 中的 ExpiresAt 保持一致。
            ProviderSessionCookie.Append(
                Response,
                authOptions.Value.PublicBaseUrl,
                cookie!,
                fromCookie.Session.ExpiresAt);
            return fromCookie.User;
        }

        var accessToken = BearerToken();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        if (string.Equals(authOptions.Value.Mode, AuthModes.Standalone, StringComparison.Ordinal))
        {
            var standalone = await standaloneSessions.ResolveAsync(accessToken, ct);
            return standalone?.User;
        }

        var record = await providerTokens.ValidateAccessAsync(accessToken, ct);
        if (record is null)
        {
            return null;
        }

        var user = await users.FindByIdAsync(record.UserId, ct);
        return user is not null && string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal)
            ? user
            : null;
    }

    private MeResponse ToResponse(AuthUserRecord user) => new()
    {
        Authenticated = true,
        User = new AuthUserDto
        {
            Platform = user.Platform,
            Uid = user.PlatformUid,
            Name = user.DisplayName,
            AvatarUrl = user.AvatarUrl
        },
        ProviderSubject = user.ProviderSubject,
        BilibiliUid = user.PlatformUid.ToString(),
        BilibiliRoomId = user.LastSourceRoomId,
        Mode = authOptions.Value.Mode,
        ReauthorizeUrl = string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal)
            ? NullIfEmpty(casdoorOptions.Value.ReauthorizeUrl)
            : null
    };

    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

[ApiController]
[Route("api/auth/provider-sessions")]
public sealed class ProviderSessionsController(
    ProviderSessionService sessions,
    ProviderTokenService providerTokens,
    IOptions<AuthOptions> authOptions,
    IOptions<CasdoorOptions> casdoorOptions) : ControllerBase
{
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Cookies[ProviderSessionService.CookieName];
        var resolved = await sessions.ResolveAsync(token, ct);
        await sessions.RevokeAsync(token, ct);

        // 账号中心退出登录：撤销该用户全部 Provider 会话与 access/refresh，避免 Casdoor/其他站点继续静默重发。
        if (resolved is not null)
        {
            await sessions.RevokeAllForUserAsync(resolved.User.Id, ct);
            await providerTokens.RevokeAllForUserAsync(resolved.User.Id, ct);
        }

        ProviderSessionCookie.Delete(Response, authOptions.Value.PublicBaseUrl);
        return Ok(new LogoutResponse { LogoutUrl = BuildCasdoorLogoutUrl() });
    }

    /// <summary>
    /// Casdoor RP-initiated logout（<c>/api/logout</c>）：结束 Casdoor SSO 会话并回跳账号中心。
    /// 未配置时返回 null，页面仅完成本地退出（PRD 的“退出统一登录”需部署时配置此项）。
    /// </summary>
    private string? BuildCasdoorLogoutUrl()
    {
        var casdoor = casdoorOptions.Value;
        if (!string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(casdoor.LogoutUrl))
        {
            return null;
        }

        var url = casdoor.LogoutUrl;
        if (!string.IsNullOrWhiteSpace(casdoor.LogoutClientId))
        {
            url = QueryHelpers.AddQueryString(url, "client_id", casdoor.LogoutClientId);
        }

        if (!string.IsNullOrWhiteSpace(casdoor.PostLogoutRedirectUri))
        {
            url = QueryHelpers.AddQueryString(url, "post_logout_redirect_uri", casdoor.PostLogoutRedirectUri);
        }

        return url;
    }
}

[ApiController]
[Route("api/auth/sessions")]
public sealed class SessionsController(
    StandaloneSessionService sessions,
    IOptions<AuthOptions> authOptions) : ControllerBase
{
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshSessionRequest? body, CancellationToken ct)
    {
        EnsureStandalone();
        if (string.IsNullOrWhiteSpace(body?.RefreshToken))
        {
            throw AuthException.Invalid("refreshToken 不能为空。");
        }

        var tokens = await sessions.RefreshAsync(body!.RefreshToken!, ct);
        return Ok(ToDto(tokens));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutSessionRequest? body, CancellationToken ct)
    {
        EnsureStandalone();
        await sessions.LogoutAsync(body?.AccessToken, body?.RefreshToken, ct);
        return NoContent();
    }

    private void EnsureStandalone()
    {
        if (!string.Equals(authOptions.Value.Mode, AuthModes.Standalone, StringComparison.Ordinal))
        {
            throw new AuthException(StatusCodes.Status404NotFound, "not_found", "Not found.");
        }
    }

    private static StandaloneTokensDto ToDto(StandaloneTokens tokens) => new()
    {
        TokenType = tokens.TokenType,
        AccessToken = tokens.AccessToken,
        ExpiresIn = tokens.ExpiresIn,
        RefreshToken = tokens.RefreshToken,
        RefreshExpiresIn = tokens.RefreshExpiresIn
    };
}
