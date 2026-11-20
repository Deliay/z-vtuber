using Microsoft.Extensions.Options;

namespace Z.Vtuber.Auth.Configuration;

public sealed class OAuthProviderOptionsValidator(IOptions<AuthOptions> authOptions)
    : IValidateOptions<OAuthProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, OAuthProviderOptions options)
    {
        var auth = authOptions.Value;
        if (!string.Equals(auth.Mode, AuthModes.Casdoor, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            errors.Add("OAuthProvider:ClientId is required in casdoor mode.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            errors.Add("OAuthProvider:ClientSecret is required in casdoor mode.");
        }

        if (!AuthOptionsValidator.IsAbsoluteHttpUrl(options.RedirectUri))
        {
            errors.Add("OAuthProvider:RedirectUri must be an absolute http(s) URL.");
        }

        if (options.AccessTokenSeconds <= 0 ||
            options.RefreshTokenDays <= 0 ||
            options.AuthorizationCodeSeconds <= 0)
        {
            errors.Add("OAuthProvider token lifetimes must all be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
