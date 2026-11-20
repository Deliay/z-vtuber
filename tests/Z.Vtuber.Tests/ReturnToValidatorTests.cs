using Z.Vtuber.Auth.Auth;

namespace Z.Vtuber.Tests;

public sealed class ReturnToValidatorTests
{
    private static readonly Z.Vtuber.Auth.Configuration.OAuthProviderOptions Provider = AuthTestOptions.Provider();

    [Fact]
    public void ValidAuthorizeUrl_ReturnsParsedRequest()
    {
        var result = ReturnToValidator.Validate(AuthTestOptions.ReturnTo(), Provider);
        Assert.Equal(Provider.ClientId, result.ClientId);
        Assert.Equal(Provider.RedirectUri, result.RedirectUri);
        Assert.Equal("state-1", result.State);
        Assert.Equal("openid profile", result.Scope);
    }

    [Fact]
    public void AcceptsPkceS256()
    {
        var challenge = Z.Vtuber.Auth.OAuth.Pkce.CreateChallenge(new string('a', 50));
        var url = AuthTestOptions.ReturnTo() + "&code_challenge=" + challenge + "&code_challenge_method=S256";
        var result = ReturnToValidator.Validate(url, Provider);
        Assert.Equal(challenge, result.CodeChallenge);
    }

    [Theory]
    [InlineData("https://evil.example/oauth/authorize?client_id=casdoor-danmaku&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback&response_type=code")]
    [InlineData("//evil.example/oauth/authorize?client_id=casdoor-danmaku&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback&response_type=code")]
    [InlineData("/account")]
    [InlineData("/oauth/authorizex?client_id=casdoor-danmaku&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback&response_type=code")]
    [InlineData("/oauth/authorize?client_id=other&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback&response_type=code")]
    [InlineData("/oauth/authorize?client_id=casdoor-danmaku&redirect_uri=https%3A%2F%2Fevil.example%2Fcallback&response_type=code")]
    [InlineData("/oauth/authorize?client_id=casdoor-danmaku&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback&response_type=token")]
    [InlineData("/oauth/authorize?client_id=casdoor-danmaku&response_type=code")]
    public void RejectsInvalidReturnTo(string returnTo)
    {
        Assert.Throws<AuthException>(() => ReturnToValidator.Validate(returnTo, Provider));
    }

    [Fact]
    public void RejectsCrLfAndFragment()
    {
        Assert.Throws<AuthException>(() => ReturnToValidator.Validate(AuthTestOptions.ReturnTo() + "\r\nX: y", Provider));
        Assert.Throws<AuthException>(() => ReturnToValidator.Validate(AuthTestOptions.ReturnTo() + "#frag", Provider));
    }

    [Fact]
    public void RejectsPlainPkce()
    {
        var url = AuthTestOptions.ReturnTo() + "&code_challenge=abc&code_challenge_method=plain";
        Assert.Throws<AuthException>(() => ReturnToValidator.Validate(url, Provider));
    }

    [Fact]
    public void AppendTicket_PreservesOriginalQuery()
    {
        var url = ReturnToValidator.AppendTicket(AuthTestOptions.ReturnTo(), "ticket-1");
        Assert.Contains("ticket=ticket-1", url);
        Assert.Contains("client_id=casdoor-danmaku", url);
        Assert.Contains("state=state-1", url);
    }
}
