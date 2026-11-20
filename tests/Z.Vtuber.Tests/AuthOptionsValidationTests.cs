using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Z.Vtuber.Auth.Configuration;

namespace Z.Vtuber.Tests;

public sealed class AuthOptionsValidationTests
{
    [Fact]
    public void ValidOptions_Pass()
    {
        var result = new AuthOptionsValidator().Validate(null, AuthTestOptions.Auth());
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void ShortPepper_Fails()
    {
        var options = AuthTestOptions.Auth();
        options.TokenPepper = "short";
        Assert.False(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void InvalidMode_Fails()
    {
        var options = AuthTestOptions.Auth();
        options.Mode = "jwt";
        Assert.False(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void InvalidPublicBaseUrl_Fails()
    {
        var options = AuthTestOptions.Auth();
        options.PublicBaseUrl = "not-a-url";
        Assert.False(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void InvalidRateLimit_Fails()
    {
        var options = AuthTestOptions.Auth();
        options.RateLimit.TokenPerMinutePerIp = 0;
        Assert.False(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void DanmakuRooms_MustBeUniqueDigits()
    {
        var options = AuthTestOptions.Danmaku("30066", "30066");
        Assert.False(new DanmakuOptionsValidator().Validate(null, options).Succeeded);

        options = AuthTestOptions.Danmaku("30066", "abc");
        Assert.False(new DanmakuOptionsValidator().Validate(null, options).Succeeded);

        options = AuthTestOptions.Danmaku("30066", "12345");
        Assert.True(new DanmakuOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void DanmakuEmptyRooms_Fail()
    {
        var options = AuthTestOptions.Danmaku();
        options.RoomIds = [];
        Assert.False(new DanmakuOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void CasdoorMode_RequiresProviderCredentials()
    {
        var auth = AuthTestOptions.Auth();
        var provider = AuthTestOptions.Provider();
        provider.ClientId = "";
        var validator = new OAuthProviderOptionsValidator(Options.Create(auth));
        Assert.False(validator.Validate(null, provider).Succeeded);
    }

    [Fact]
    public void StandaloneMode_DoesNotRequireProviderCredentials()
    {
        var auth = AuthTestOptions.Auth(AuthModes.Standalone);
        var provider = AuthTestOptions.Provider();
        provider.ClientId = "";
        provider.ClientSecret = "";
        provider.RedirectUri = "";
        var validator = new OAuthProviderOptionsValidator(Options.Create(auth));
        Assert.True(validator.Validate(null, provider).Succeeded);
    }

    [Fact]
    public void ProviderSessionCookie_SecureFollowsPublicBaseUrl()
    {
        Assert.True(Z.Vtuber.Auth.Auth.ProviderSessionCookie.IsSecure("https://auth.example.com"));
        Assert.False(Z.Vtuber.Auth.Auth.ProviderSessionCookie.IsSecure("http://localhost:8087"));

        var options = Z.Vtuber.Auth.Auth.ProviderSessionCookie.BuildOptions(
            "https://auth.example.com", DateTime.UtcNow.AddDays(1));
        Assert.True(options.HttpOnly);
        Assert.True(options.Secure);
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
        Assert.Equal("/", options.Path);
    }
}
