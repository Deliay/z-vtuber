using Microsoft.Extensions.Options;

namespace Z.Vtuber.Auth.Configuration;

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();

        if (options.Mode is not (AuthModes.Casdoor or AuthModes.Standalone))
        {
            errors.Add($"Auth:Mode must be '{AuthModes.Casdoor}' or '{AuthModes.Standalone}'.");
        }

        if (string.IsNullOrWhiteSpace(options.TokenPepper) || options.TokenPepper.Length < 32)
        {
            errors.Add("Auth:TokenPepper is required and must be at least 32 characters.");
        }

        if (!IsAbsoluteHttpUrl(options.PublicBaseUrl))
        {
            errors.Add("Auth:PublicBaseUrl must be an absolute http(s) URL.");
        }

        if (!IsRelativePath(options.AccountCenterUrl) && !IsAbsoluteHttpUrl(options.AccountCenterUrl))
        {
            errors.Add("Auth:AccountCenterUrl must be a relative path starting with '/' or an absolute http(s) URL.");
        }

        if (options.LoginTokenTtlSeconds <= 0)
        {
            errors.Add("Auth:LoginTokenTtlSeconds must be positive.");
        }

        if (options.ConfirmWindowSeconds <= 0)
        {
            errors.Add("Auth:ConfirmWindowSeconds must be positive.");
        }

        if (options.ConfirmOperationTimeoutSeconds <= 0)
        {
            errors.Add("Auth:ConfirmOperationTimeoutSeconds must be positive.");
        }

        if (options.ProviderTicketSeconds <= 0)
        {
            errors.Add("Auth:ProviderTicketSeconds must be positive.");
        }

        if (options.ProviderSessionDays <= 0)
        {
            errors.Add("Auth:ProviderSessionDays must be positive.");
        }

        var rate = options.RateLimit;
        if (rate.TokenPerMinutePerIp <= 0 ||
            rate.TokenPerDayPerIp <= 0 ||
            rate.TokenPerHourPerClient <= 0 ||
            rate.DanmakuPerMinutePerUid <= 0 ||
            rate.OAuthTokenPerMinutePerClient <= 0)
        {
            errors.Add("Auth:RateLimit values must all be positive.");
        }

        if (options.Standalone.AccessTokenSeconds <= 0 || options.Standalone.RefreshTokenDays <= 0)
        {
            errors.Add("Auth:Standalone token lifetimes must be positive.");
        }

        foreach (var origin in options.AllowedOrigins)
        {
            if (!IsAbsoluteHttpUrl(origin))
            {
                errors.Add($"Auth:AllowedOrigins contains an invalid origin: {origin}");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    internal static bool IsAbsoluteHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal static bool IsRelativePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') &&
        !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.Contains('\r') &&
        !value.Contains('\n');
}
