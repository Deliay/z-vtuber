using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.OAuth;

[ApiController]
public sealed class OAuthUserInfoController(
    IOptions<AuthOptions> authOptions,
    ProviderTokenService tokens,
    UserRepository users) : ControllerBase
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
            return UnauthorizedResponse();
        }

        var user = await users.FindByIdAsync(record.UserId, ct);
        if (user is null || !string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal))
        {
            return UnauthorizedResponse();
        }

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
}

[ApiController]
public sealed class OAuthLogoutController(
    IOptions<AuthOptions> authOptions,
    IOptions<OAuthProviderOptions> providerOptions,
    ProviderTokenService tokens,
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

        var provider = providerOptions.Value;
        var providedId = client_id;
        var providedSecret = client_secret;
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
                return UnauthorizedClient();
            }
        }

        if ((!string.IsNullOrEmpty(providedId) || !string.IsNullOrEmpty(providedSecret)) &&
            (!TokenHasher.FixedTimeEquals(providedId, provider.ClientId) ||
             !TokenHasher.FixedTimeEquals(providedSecret, provider.ClientSecret)))
        {
            return UnauthorizedClient();
        }

        await tokens.RevokeAsync(token, refresh_token, ct);
        logger.LogInformation("OAuth logout processed for client {ClientId}.", providedId ?? "unknown");
        return Ok(new { });
    }

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
