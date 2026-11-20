using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Z.Vtuber.Auth.Configuration;

namespace Z.Vtuber.Auth.Auth;

/// <summary>已校验的 OAuth 授权请求参数（来自 <c>returnTo</c>）。</summary>
public sealed record AuthorizeRequest(
    string ClientId,
    string RedirectUri,
    string? State,
    string Scope,
    string? CodeChallenge,
    string? CodeChallengeMethod);

/// <summary>
/// <c>returnTo</c> 校验：只允许本服务 <c>/oauth/authorize</c> 相对 URL，并逐项校验授权参数，防开放重定向。
/// </summary>
public static class ReturnToValidator
{
    public const string AuthorizePath = "/oauth/authorize";

    public static AuthorizeRequest Validate(string? returnTo, OAuthProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(returnTo))
        {
            throw AuthException.Invalid("缺少 returnTo，请从站点登录入口进入。");
        }

        if (returnTo.Contains('\r') ||
            returnTo.Contains('\n') ||
            returnTo.Contains('\\') ||
            returnTo.Contains('#'))
        {
            throw AuthException.Invalid("returnTo 非法。");
        }

        if (!returnTo.StartsWith('/') || returnTo.StartsWith("//", StringComparison.Ordinal))
        {
            throw AuthException.Invalid("returnTo 必须是本服务相对路径。");
        }

        var queryIndex = returnTo.IndexOf('?');
        var path = queryIndex >= 0 ? returnTo[..queryIndex] : returnTo;
        if (!string.Equals(path, AuthorizePath, StringComparison.Ordinal))
        {
            throw AuthException.Invalid("returnTo 只允许指向 /oauth/authorize。");
        }

        var query = queryIndex >= 0 ? returnTo[(queryIndex + 1)..] : string.Empty;
        var parsed = QueryHelpers.ParseQuery(query);

        var clientId = Single(parsed, "client_id");
        var redirectUri = Single(parsed, "redirect_uri");
        var responseType = Single(parsed, "response_type");

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(redirectUri) ||
            string.IsNullOrWhiteSpace(responseType))
        {
            throw AuthException.Invalid("returnTo 缺少 client_id/redirect_uri/response_type。");
        }

        if (!TokenHasher.FixedTimeEquals(clientId, provider.ClientId))
        {
            throw AuthException.Invalid("returnTo 的 client_id 非法。");
        }

        if (!string.Equals(redirectUri, provider.RedirectUri, StringComparison.Ordinal))
        {
            throw AuthException.Invalid("returnTo 的 redirect_uri 非法。");
        }

        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            throw AuthException.Invalid("returnTo 的 response_type 必须是 code。");
        }

        var scope = Single(parsed, "scope");
        var challenge = Single(parsed, "code_challenge");
        var challengeMethod = Single(parsed, "code_challenge_method");
        if (!string.IsNullOrEmpty(challenge) &&
            !string.Equals(challengeMethod, "S256", StringComparison.Ordinal))
        {
            throw AuthException.Invalid("仅支持 PKCE S256。");
        }

        return new AuthorizeRequest(
            clientId,
            redirectUri,
            Single(parsed, "state"),
            string.IsNullOrWhiteSpace(scope) ? "openid profile" : scope,
            string.IsNullOrEmpty(challenge) ? null : challenge,
            string.IsNullOrEmpty(challenge) ? null : challengeMethod);
    }

    /// <summary>把一次性 ticket 追加到原有 returnTo（保留所有原 query 参数）。</summary>
    public static string AppendTicket(string returnTo, string ticket) =>
        QueryHelpers.AddQueryString(returnTo, "ticket", ticket);

    private static string? Single(Dictionary<string, StringValues> query, string key) =>
        query.TryGetValue(key, out var values) && values.Count > 0
            ? values.ToString()
            : null;
}
