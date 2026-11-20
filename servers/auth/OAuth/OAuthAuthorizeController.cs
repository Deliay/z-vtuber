using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.OAuth;

[ApiController]
public sealed class OAuthAuthorizeController(
    IOptions<AuthOptions> authOptions,
    IOptions<OAuthProviderOptions> providerOptions,
    ProviderSessionService sessions,
    ProviderTicketService tickets,
    AuthorizationCodeService codes,
    UserRepository users,
    TimeProvider clock,
    ILogger<OAuthAuthorizeController> logger) : ControllerBase
{
    [HttpGet("/oauth/authorize")]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        if (!string.Equals(authOptions.Value.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var provider = providerOptions.Value;
        var query = Request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();
        var responseType = query["response_type"].ToString();
        var state = query["state"].ToString();
        var scope = query["scope"].ToString();
        var challenge = query["code_challenge"].ToString();
        var challengeMethod = query["code_challenge_method"].ToString();

        if (!TokenHasher.FixedTimeEquals(clientId, provider.ClientId) ||
            !string.Equals(redirectUri, provider.RedirectUri, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "invalid_request", error_description = "Invalid client_id or redirect_uri." });
        }

        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            return RedirectWithError(redirectUri, "unsupported_response_type", state);
        }

        // 用户在验证页选择「不是我/取消」：带 error=access_denied 回到已校验的 redirect_uri。
        if (IsDenied(query["denied"]))
        {
            return RedirectWithError(redirectUri, "access_denied", state);
        }

        if (!string.IsNullOrEmpty(challenge) &&
            !string.Equals(challengeMethod, Pkce.MethodS256, StringComparison.Ordinal))
        {
            return RedirectWithError(redirectUri, "invalid_request", state);
        }

        var request = new AuthorizeRequest(
            clientId,
            redirectUri,
            string.IsNullOrEmpty(state) ? null : state,
            string.IsNullOrWhiteSpace(scope) ? "openid profile" : scope,
            string.IsNullOrEmpty(challenge) ? null : challenge,
            string.IsNullOrEmpty(challenge) ? null : challengeMethod);

        var ticketValue = query["ticket"].ToString();
        if (!string.IsNullOrEmpty(ticketValue))
        {
            var ticket = await tickets.ConsumeAsync(ticketValue, clientId, redirectUri, ct);
            if (ticket is null)
            {
                // 重放/过期 ticket：不签发 code，也不静默降级为已有会话。
                return BadRequest(new { error = "invalid_grant", error_description = "The provider ticket is invalid or already used." });
            }

            if (!TokenHasher.FixedTimeEquals(ticket.State, state))
            {
                return BadRequest(new { error = "invalid_grant", error_description = "The provider ticket state does not match." });
            }

            var user = await users.FindByIdAsync(ticket.UserId, ct);
            if (user is null || !string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal))
            {
                return RedirectWithError(redirectUri, "access_denied", state);
            }

            var sessionToken = await sessions.CreateAsync(user.Id, ClientIp(), Request.Headers.UserAgent.ToString(), ct);
            ProviderSessionCookie.Append(
                Response, authOptions.Value.PublicBaseUrl, sessionToken, clock.GetUtcNow().UtcDateTime.AddDays(authOptions.Value.ProviderSessionDays));

            var ticketRequest = new AuthorizeRequest(
                ticket.ClientId,
                ticket.RedirectUri,
                string.IsNullOrEmpty(ticket.State) ? null : ticket.State,
                ticket.Scope,
                ticket.CodeChallenge,
                ticket.CodeChallengeMethod);
            var code = await codes.IssueAsync(user.Id, ticketRequest, ticket.Id, ct);

            Response.Headers["Referrer-Policy"] = "no-referrer";
            logger.LogInformation("OAuth authorize completed via provider ticket for user {UserId}.", user.Id);
            return RedirectWithCode(redirectUri, code, state);
        }

        var existing = await sessions.ResolveAsync(Request.Cookies[ProviderSessionService.CookieName], ct);
        if (existing is not null)
        {
            var code = await codes.IssueAsync(existing.User.Id, request, existing.Session.Id, ct);
            logger.LogInformation("OAuth authorize completed via provider session for user {UserId}.", existing.User.Id);
            return RedirectWithCode(redirectUri, code, state);
        }

        var accountCenter = authOptions.Value.AccountCenterUrl;
        var returnTo = Request.Path + Request.QueryString;
        var location = QueryHelpers.AddQueryString(accountCenter, "return_to", returnTo);
        return Redirect(location);
    }

    private IActionResult RedirectWithCode(string redirectUri, string code, string? state)
    {
        var url = QueryHelpers.AddQueryString(redirectUri, "code", code);
        if (!string.IsNullOrEmpty(state))
        {
            url = QueryHelpers.AddQueryString(url, "state", state);
        }

        return Redirect(url);
    }

    private IActionResult RedirectWithError(string redirectUri, string error, string? state)
    {
        var url = QueryHelpers.AddQueryString(redirectUri, "error", error);
        if (!string.IsNullOrEmpty(state))
        {
            url = QueryHelpers.AddQueryString(url, "state", state);
        }

        return Redirect(url);
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool IsDenied(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
