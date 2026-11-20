using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.Middleware;
using Z.Vtuber.Auth.OAuth;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Tests;

/// <summary>
/// Phase 8.7：明文泄漏、重放/篡改与错误体分离安全用例。
/// </summary>
public sealed class AuthSecurityTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;

    public AuthSecurityTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DatabaseAndAudit_DoNotStorePlaintextSecrets()
    {
        var created = await _services.LoginRequestService.CreateAsync(
            "account-center", "nonce-value", AuthTestOptions.ReturnTo(), "127.0.0.1", "ua", default);
        await _services.LoginRequests.TryMarkVerifiedAsync(
            _services.Hasher.Hash(created.Token), "30066",
            new DanmakuSender(created.Token, 403496, "Zero_Ash", "https://i2.hdslb.com/face.jpg", string.Empty),
            _services.Clock.GetUtcNow().UtcDateTime,
            120);
        var confirm = await _services.LoginRequestService.ConfirmAsync(
            created.RequestId, created.PollToken, "127.0.0.1", "ua", default);

        var ticket = ParseQuery(confirm.RedirectUrl!, "ticket");
        var request = ReturnToValidator.Validate(AuthTestOptions.ReturnTo(), _services.ProviderOptions);
        var code = await _services.CodeService.IssueAsync(confirm.UserId, request, null);
        var tokens = await _services.TokenService.IssueAsync(
            confirm.UserId, _services.ProviderOptions.ClientId, "openid profile");

        var dump = await DumpAllCollectionsAsync();
        foreach (var secret in new[]
                 {
                     created.Token, created.PollToken, ticket,
                     code, tokens.AccessToken, tokens.RefreshToken
                 })
        {
            Assert.DoesNotContain(secret, dump, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TamperedOrReplayedTicketCodeAndToken_AreRejected()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(42, "u", null, "30066", null);

        var ticket = await _services.TicketService.CreateAsync(user.User.Id, AuthTestOptions.ReturnTo());
        Assert.Null(await _services.TicketService.ConsumeAsync(
            ticket.Ticket + "x", _services.ProviderOptions.ClientId, _services.ProviderOptions.RedirectUri));
        Assert.NotNull(await _services.TicketService.ConsumeAsync(
            ticket.Ticket, _services.ProviderOptions.ClientId, _services.ProviderOptions.RedirectUri));
        Assert.Null(await _services.TicketService.ConsumeAsync(
            ticket.Ticket, _services.ProviderOptions.ClientId, _services.ProviderOptions.RedirectUri));

        var request = ReturnToValidator.Validate(AuthTestOptions.ReturnTo(), _services.ProviderOptions);
        var code = await _services.CodeService.IssueAsync(user.User.Id, request, null);
        await Assert.ThrowsAsync<OAuthException>(() => _services.CodeService.ConsumeAsync(
            code + "x", request.ClientId, request.RedirectUri, null));

        var pair = await _services.TokenService.IssueAsync(
            user.User.Id, _services.ProviderOptions.ClientId, "openid profile");
        Assert.Null(await _services.TokenService.ValidateAccessAsync(pair.AccessToken + "x"));
    }

    [Fact]
    public async Task UnexpectedOAuthError_UsesStandardBody_WhileAccountApiUsesAuthBody()
    {
        var oauth = await InvokeThrowingAsync("/oauth/token");
        Assert.Equal(StatusCodes.Status500InternalServerError, oauth.Status);
        using var oauthJson = JsonDocument.Parse(oauth.Body);
        Assert.Equal("server_error", oauthJson.RootElement.GetProperty("error").GetString());
        Assert.True(oauthJson.RootElement.TryGetProperty("error_description", out _));
        Assert.False(oauthJson.RootElement.TryGetProperty("code", out _));

        var account = await InvokeThrowingAsync("/api/auth/me");
        Assert.Equal(StatusCodes.Status500InternalServerError, account.Status);
        using var accountJson = JsonDocument.Parse(account.Body);
        Assert.Equal("AUTH_INTERNAL", accountJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<(int Status, string Body)> InvokeThrowingAsync(string path)
    {
        var middleware = new AuthExceptionMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<AuthExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private async Task<string> DumpAllCollectionsAsync()
    {
        var collections = new[]
        {
            AuthCollectionNames.LoginRequests,
            AuthCollectionNames.Users,
            AuthCollectionNames.ProviderSessions,
            AuthCollectionNames.ProviderTickets,
            AuthCollectionNames.OAuthCodes,
            AuthCollectionNames.ProviderTokens,
            AuthCollectionNames.Sessions,
            AuthCollectionNames.AuditLogs
        };

        var builder = new StringBuilder();
        foreach (var name in collections)
        {
            var documents = await _fixture.Database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync();
            foreach (var document in documents)
            {
                builder.AppendLine(document.ToJson());
            }
        }

        return builder.ToString();
    }

    private static string ParseQuery(string url, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            new Uri(new Uri("http://localhost"), url).Query)[name].ToString();
}
