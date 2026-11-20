using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Observability;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.OAuth;

[ApiController]
public sealed class OAuthTokenController(
    IOptions<AuthOptions> authOptions,
    IOptions<OAuthProviderOptions> providerOptions,
    AuthorizationCodeService codes,
    ProviderTokenService tokens,
    IAuthRateLimiter rateLimiter,
    UserRepository users,
    AuditLogger audit,
    AuthMetrics metrics,
    ILogger<OAuthTokenController> logger) : ControllerBase
{
    [HttpPost("/oauth/token")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(
        [FromForm] string? grant_type,
        [FromForm] string? code,
        [FromForm] string? redirect_uri,
        [FromForm] string? code_verifier,
        [FromForm] string? refresh_token,
        [FromForm] string? client_id,
        [FromForm] string? client_secret,
        CancellationToken ct)
    {
        if (!string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
        {
            return NotFound();
        }

        try
        {
            // 限流先于 client 认证，失败尝试同样计数；键为 client + IP。
            if (!await rateLimiter.TryOAuthTokenAsync(
                    string.IsNullOrWhiteSpace(client_id) ? "unknown" : client_id,
                    ClientIp(),
                    ct))
            {
                metrics.OAuthFailure();
                await audit.LogAsync(
                    AuthAuditEvents.OAuthTokenIssued,
                    clientIp: ClientIp(),
                    userAgent: Request.Headers.UserAgent.ToString(),
                    detail: "rate_limited",
                    ct: ct);
                return TooManyRequests();
            }

            var client = Authenticate(client_id, client_secret);
            if (string.IsNullOrEmpty(grant_type))
            {
                throw OAuthException.InvalidRequest("grant_type is required.");
            }

            ProviderTokenPair pair;
            if (string.Equals(grant_type, "authorization_code", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirect_uri))
                {
                    throw OAuthException.InvalidRequest("code and redirect_uri are required.");
                }

                var record = await codes.ConsumeAsync(code, client, redirect_uri, code_verifier, ct);
                await EnsureActiveUserAsync(record.UserId, ct);
                pair = await tokens.IssueAsync(record.UserId, record.ClientId, record.Scope, ct);
            }
            else if (string.Equals(grant_type, "refresh_token", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(refresh_token))
                {
                    throw OAuthException.InvalidRequest("refresh_token is required.");
                }

                pair = await tokens.RotateAsync(refresh_token, client, ct);
            }
            else
            {
                throw OAuthException.UnsupportedGrantType();
            }

            logger.LogInformation(
                "OAuth token issued for client {ClientId} via {GrantType}.", client, grant_type);
            metrics.OAuthToken();
            var issuedUser = await users.FindByIdAsync(pair.UserId, ct);
            await audit.LogAsync(
                AuthAuditEvents.OAuthTokenIssued,
                platform: "bilibili",
                platformUid: issuedUser?.PlatformUid,
                clientIp: ClientIp(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detail: grant_type,
                ct: ct);
            return Ok(new
            {
                access_token = pair.AccessToken,
                refresh_token = pair.RefreshToken,
                token_type = "Bearer",
                expires_in = pair.ExpiresIn,
                scope = pair.Scope
            });
        }
        catch (OAuthException ex)
        {
            metrics.OAuthFailure();
            await audit.LogAsync(
                AuthAuditEvents.OAuthTokenIssued,
                clientIp: ClientIp(),
                userAgent: Request.Headers.UserAgent.ToString(),
                detail: ex.Error,
                ct: ct);
            var status = string.Equals(ex.Error, "invalid_client", StringComparison.Ordinal)
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status400BadRequest;
            if (status == StatusCodes.Status401Unauthorized)
            {
                Response.Headers.WWWAuthenticate = "Basic realm=\"oauth\"";
            }

            return StatusCode(status, new { error = ex.Error, error_description = ex.Description });
        }
    }

    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private string Authenticate(string? formClientId, string? formClientSecret)
    {
        var provider = providerOptions.Value;
        string? clientId = formClientId;
        string? clientSecret = formClientSecret;

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
                    clientId = decoded[..separator];
                    clientSecret = decoded[(separator + 1)..];
                }
            }
            catch (FormatException)
            {
                throw OAuthException.InvalidClient();
            }
        }

        if (!TokenHasher.FixedTimeEquals(clientId, provider.ClientId) ||
            !TokenHasher.FixedTimeEquals(clientSecret, provider.ClientSecret))
        {
            throw OAuthException.InvalidClient();
        }

        return provider.ClientId;
    }

    private async Task EnsureActiveUserAsync(MongoDB.Bson.ObjectId userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null || !string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal))
        {
            throw OAuthException.InvalidGrant("The user is not active.");
        }
    }

    private IActionResult TooManyRequests()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = "too_many_requests",
            error_description = "Too many token requests."
        });
    }
}
