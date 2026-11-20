namespace Z.Vtuber.Auth.OAuth;

/// <summary>OAuth 标准错误（<c>{ error, error_description }</c>）。</summary>
public sealed class OAuthException(string error, string description) : Exception(description)
{
    public string Error { get; } = error;

    public string Description { get; } = description;

    public static OAuthException InvalidClient() => new("invalid_client", "Client authentication failed.");

    public static OAuthException InvalidGrant(string description = "The grant is invalid or expired.") =>
        new("invalid_grant", description);

    public static OAuthException InvalidRequest(string description) => new("invalid_request", description);

    public static OAuthException UnsupportedGrantType() =>
        new("unsupported_grant_type", "Unsupported grant_type.");

    public static OAuthException TooManyRequests() =>
        new("too_many_requests", "Too many requests.");
}
