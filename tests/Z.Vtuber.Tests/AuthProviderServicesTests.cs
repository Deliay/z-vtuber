using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.OAuth;

namespace Z.Vtuber.Tests;

public sealed class AuthProviderServicesTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;

    public AuthProviderServicesTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProviderSession_CreateResolveRevoke()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(1, "u1", null, "30066", null);
        var token = await _services.SessionService.CreateAsync(user.User.Id, "127.0.0.1", "ua");

        var resolved = await _services.SessionService.ResolveAsync(token);
        Assert.NotNull(resolved);
        Assert.Equal(user.User.Id, resolved!.User.Id);

        await _services.SessionService.RevokeAsync(token);
        Assert.Null(await _services.SessionService.ResolveAsync(token));
    }

    [Fact]
    public async Task ProviderSession_Expires()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(2, "u2", null, "30066", null);
        var token = await _services.SessionService.CreateAsync(user.User.Id, null, null);
        _services.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Null(await _services.SessionService.ResolveAsync(token));
    }

    [Fact]
    public async Task Ticket_IsOneTimeAndBoundToAuthorizeParameters()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(3, "u3", null, "30066", null);
        var created = await _services.TicketService.CreateAsync(user.User.Id, AuthTestOptions.ReturnTo());

        Assert.Contains("ticket=", created.RedirectUrl);
        var consumed = await _services.TicketService.ConsumeAsync(
            created.Ticket, _services.ProviderOptions.ClientId, _services.ProviderOptions.RedirectUri);
        Assert.NotNull(consumed);
        Assert.Equal(user.User.Id, consumed!.UserId);

        Assert.Null(await _services.TicketService.ConsumeAsync(
            created.Ticket, _services.ProviderOptions.ClientId, _services.ProviderOptions.RedirectUri));
    }

    [Fact]
    public async Task Ticket_ExpiresAfterSixtySeconds()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(4, "u4", null, "30066", null);
        var created = await _services.TicketService.CreateAsync(user.User.Id, AuthTestOptions.ReturnTo());
        _services.Clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(await _services.TicketService.ConsumeAsync(
            created.Ticket, _services.ProviderOptions.ClientId, _services.ProviderOptions.RedirectUri));
    }

    [Fact]
    public async Task AuthorizationCode_IsOneTime_AndValidatesRedirect()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(5, "u5", null, "30066", null);
        var request = ReturnToValidator.Validate(AuthTestOptions.ReturnTo(), _services.ProviderOptions);
        var code = await _services.CodeService.IssueAsync(user.User.Id, request, null);

        await Assert.ThrowsAsync<OAuthException>(() =>
            _services.CodeService.ConsumeAsync(code, request.ClientId, "https://evil.example/callback", null));

        var record = await _services.CodeService.ConsumeAsync(
            code, request.ClientId, request.RedirectUri, null);
        Assert.Equal(user.User.Id, record.UserId);

        await Assert.ThrowsAsync<OAuthException>(() =>
            _services.CodeService.ConsumeAsync(code, request.ClientId, request.RedirectUri, null));
    }

    [Fact]
    public async Task AuthorizationCode_ValidatesPkce()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(6, "u6", null, "30066", null);
        var verifier = new string('a', 50);
        var request = new AuthorizeRequest(
            _services.ProviderOptions.ClientId,
            _services.ProviderOptions.RedirectUri,
            "state",
            "openid profile",
            Pkce.CreateChallenge(verifier),
            Pkce.MethodS256);
        var code = await _services.CodeService.IssueAsync(user.User.Id, request, null);

        await Assert.ThrowsAsync<OAuthException>(() => _services.CodeService.ConsumeAsync(
            code, request.ClientId, request.RedirectUri, new string('b', 50)));

        var record = await _services.CodeService.ConsumeAsync(
            code, request.ClientId, request.RedirectUri, verifier);
        Assert.NotNull(record);
    }

    [Fact]
    public async Task ProviderToken_IssueValidateRotate()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(7, "u7", null, "30066", null);
        var pair = await _services.TokenService.IssueAsync(
            user.User.Id, _services.ProviderOptions.ClientId, "openid profile");

        Assert.NotNull(await _services.TokenService.ValidateAccessAsync(pair.AccessToken));

        var rotated = await _services.TokenService.RotateAsync(pair.RefreshToken, _services.ProviderOptions.ClientId);
        Assert.NotEqual(pair.AccessToken, rotated.AccessToken);
        Assert.NotEqual(pair.RefreshToken, rotated.RefreshToken);
        Assert.Null(await _services.TokenService.ValidateAccessAsync(pair.AccessToken));
        Assert.NotNull(await _services.TokenService.ValidateAccessAsync(rotated.AccessToken));

        await Assert.ThrowsAsync<OAuthException>(() =>
            _services.TokenService.RotateAsync(pair.RefreshToken, _services.ProviderOptions.ClientId));
    }

    [Fact]
    public async Task StandaloneSession_RefreshRotatesAndRevokes()
    {
        var standalone = AuthTestServices.Create(_fixture.Database, mode: AuthModes.Standalone);
        var user = await standalone.UserService.UpsertBilibiliUserAsync(8, "u8", null, "30066", null);
        var tokens = await standalone.StandaloneService.IssueAsync(user.User.Id, null, null);

        var refreshed = await standalone.StandaloneService.RefreshAsync(tokens.RefreshToken);
        Assert.NotEqual(tokens.AccessToken, refreshed.AccessToken);
        Assert.Null(await standalone.StandaloneService.ResolveAsync(tokens.AccessToken));
        Assert.NotNull(await standalone.StandaloneService.ResolveAsync(refreshed.AccessToken));

        await standalone.StandaloneService.LogoutAsync(refreshed.AccessToken, refreshed.RefreshToken);
        Assert.Null(await standalone.StandaloneService.ResolveAsync(refreshed.AccessToken));
    }
}
