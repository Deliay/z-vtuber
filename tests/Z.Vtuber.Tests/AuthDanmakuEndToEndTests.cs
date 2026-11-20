using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Z.Vtuber.Auth.Contracts;
using Z.Vtuber.Tests.AuthFakes;

namespace Z.Vtuber.Tests;

/// <summary>
/// Phase 9.3：真实 Kestrel + 假 SSE 源 + Mongo/Redis 的多房间端到端链路。
/// </summary>
public sealed class AuthDanmakuEndToEndTests : IClassFixture<MongoDbFixture>, IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _mongo;
    private readonly RedisFixture _redis;
    private FakeDanmakuSource _source = null!;

    public AuthDanmakuEndToEndTests(MongoDbFixture mongo, RedisFixture redis)
    {
        _mongo = mongo;
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        await _mongo.ResetAsync();
        _source = new FakeDanmakuSource();
        await _source.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
    }

    private Task<AuthApiFactory> StartAsync(string[] rooms, IReadOnlyDictionary<string, string?>? overrides = null) =>
        AuthApiFactory.StartAsync(
            _mongo.ConnectionString,
            _mongo.Database.DatabaseNamespace.DatabaseName,
            _redis.ConnectionString,
            _source,
            mode: "casdoor",
            roomIds: rooms,
            overrides: overrides);

    [Fact]
    public async Task SingleRoom_FullTicketFlow_ReachesUserInfo()
    {
        await using var factory = await StartAsync(["30066"]);
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");

        var created = await CreateLoginAsync(factory);
        await _source.PublishDanmakuAsync("30066", created.Token);
        var verified = await WaitForStatusAsync(factory, created, "verified");
        Assert.Equal("30066", verified.SourceRoomId);
        Assert.Equal(403496, verified.User!.Uid);

        var confirm = await ConfirmAsync(factory, created);
        Assert.Equal("casdoor", confirm.Mode);
        Assert.NotNull(confirm.RedirectUrl);

        var authorize = await factory.Client.GetAsync(confirm.RedirectUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var callback = authorize.Headers.Location
            ?? throw new InvalidOperationException("Authorize did not redirect to the client callback.");
        Assert.StartsWith(AuthTestOptions.Provider().RedirectUri, callback.ToString());
        var code = ParseQuery(callback, "code");

        var tokens = await ExchangeCodeAsync(factory, code);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var userInfo = await UserInfoAsync(factory, accessToken);
        Assert.Equal("bili_403496", userInfo.GetProperty("sub").GetString());
        Assert.Equal("30066", userInfo.GetProperty("bilibili_room_id").GetString());
    }

    [Fact]
    public async Task MultiRoom_TokenInSecondaryRoom_Verifies()
    {
        await using var factory = await StartAsync(["30066", "12345"]);
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066", "12345");

        var created = await CreateLoginAsync(factory);
        await _source.PublishDanmakuAsync("12345", created.Token, uid: 9001, name: "Secondary");
        var verified = await WaitForStatusAsync(factory, created, "verified");
        Assert.Equal("12345", verified.SourceRoomId);
        Assert.Equal(9001, verified.User!.Uid);
    }

    [Fact]
    public async Task UnconfiguredRoom_IsIgnored()
    {
        await using var factory = await StartAsync(["30066"]);
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");

        var created = await CreateLoginAsync(factory);
        await _source.PublishDanmakuAsync("99999", created.Token);

        await Task.Delay(500);
        var status = await GetStatusAsync(factory, created);
        Assert.Equal("pending", status.Status);
    }

    [Fact]
    public async Task AllRoomsUnavailable_Returns503WithoutToken()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Auth:Danmaku:BaseUrl"] = "http://127.0.0.1:1"
        };
        await using var factory = await StartAsync(["30066"], overrides);

        var rooms = await factory.GetRoomsAsync();
        Assert.All(rooms.Rooms, room => Assert.False(room.Available));

        var response = await factory.Client.PostAsJsonAsync("/api/auth/login-requests", new CreateLoginRequestRequest
        {
            ClientId = "account-center",
            ReturnTo = AuthTestOptions.ReturnTo()
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("AUTH_DANMAKU_SOURCE_UNAVAILABLE", body!.Error.Code);
    }

    [Fact]
    public async Task StandaloneFlow_ConfirmsWithOpaqueTokensAndSessionCookie()
    {
        await using var factory = await StartAsync(["30066"], overrides: new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "standalone"
        });
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");

        var createResponse = await factory.Client.PostAsJsonAsync("/api/auth/login-requests", new CreateLoginRequestRequest
        {
            ClientId = "account-center"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<CreatedLoginRequestResponse>())!;

        await _source.PublishDanmakuAsync("30066", created.Token);
        await WaitForStatusAsync(factory, created, "verified");

        using var confirmRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/auth/login-requests/{created.RequestId}/confirm");
        confirmRequest.Headers.Add("X-Poll-Token", created.PollToken);
        var confirmResponse = await factory.Client.SendAsync(confirmRequest);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        Assert.Contains("auth_provider_session=", confirmResponse.Headers.GetValues("Set-Cookie").Single());
        var confirm = (await confirmResponse.Content.ReadFromJsonAsync<ConfirmLoginResponse>())!;
        Assert.Equal("standalone", confirm.Mode);
        Assert.NotNull(confirm.Tokens);
        Assert.Equal(900, confirm.Tokens!.ExpiresIn);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirm.Tokens.AccessToken);
        var meResponse = await factory.Client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = (await meResponse.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.True(me.Authenticated);
        Assert.Equal("bili_403496", me.ProviderSubject);
    }

    [Fact]
    public async Task Cors_AppliesOnlyToAccountCenterApi()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Auth:AllowedOrigins:0"] = "https://site.example"
        };
        await using var factory = await StartAsync(["30066"], overrides);
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");

        var account = await PreflightAsync(factory, "/api/auth/login-requests");
        Assert.True(account.Headers.Contains("Access-Control-Allow-Origin"));

        var oauth = await PreflightAsync(factory, "/oauth/token");
        Assert.False(oauth.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static async Task<HttpResponseMessage> PreflightAsync(AuthApiFactory factory, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", "https://site.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return await factory.Client.SendAsync(request);
    }

    private static async Task<CreatedLoginRequestResponse> CreateLoginAsync(AuthApiFactory factory)
    {
        var response = await factory.Client.PostAsJsonAsync("/api/auth/login-requests", new CreateLoginRequestRequest
        {
            ClientId = "account-center",
            ReturnTo = AuthTestOptions.ReturnTo()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedLoginRequestResponse>())!;
    }

    internal static async Task<LoginRequestStatusResponse> GetStatusAsync(
        AuthApiFactory factory, CreatedLoginRequestResponse created)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/auth/login-requests/{created.RequestId}");
        request.Headers.Add("X-Poll-Token", created.PollToken);
        var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginRequestStatusResponse>())!;
    }

    internal static async Task<LoginRequestStatusResponse> WaitForStatusAsync(
        AuthApiFactory factory, CreatedLoginRequestResponse created, string expected)
    {
        LoginRequestStatusResponse? last = null;
        await TestWait.UntilAsync(async () =>
        {
            last = await GetStatusAsync(factory, created);
            return last.Status == expected;
        }, TimeSpan.FromSeconds(10), describe: $"login request status == {expected}");

        return last!;
    }

    internal static async Task<ConfirmLoginResponse> ConfirmAsync(
        AuthApiFactory factory, CreatedLoginRequestResponse created)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/auth/login-requests/{created.RequestId}/confirm");
        request.Headers.Add("X-Poll-Token", created.PollToken);
        var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConfirmLoginResponse>())!;
    }

    internal static async Task<JsonElement> ExchangeCodeAsync(AuthApiFactory factory, string code)
    {
        var provider = AuthTestOptions.Provider();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = provider.RedirectUri
            })
        };
        var basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{provider.ClientId}:{provider.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    internal static async Task<JsonElement> UserInfoAsync(AuthApiFactory factory, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    internal static string ParseQuery(Uri uri, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)[name].ToString();
}
