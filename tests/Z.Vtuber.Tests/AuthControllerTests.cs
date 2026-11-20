using Z.Vtuber.Auth.Persistence;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Contracts;
using Z.Vtuber.Auth.Controllers;
using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.OAuth;

namespace Z.Vtuber.Tests;

public sealed class AuthPageTests
{
    private sealed class TestWebHostEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Z.Vtuber.Auth";
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);
        public string WebRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);
    }

    private static PagesController Create()
    {
        var webRoot = Path.Combine(FindRepositoryRoot(), "servers", "auth", "wwwroot");
        var controller = new PagesController(
            new TestWebHostEnvironment(webRoot),
            NullLogger<PagesController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task ConnectPage_ReturnsHtmlAndSecurityHeaders()
    {
        var controller = Create();
        var result = await controller.Connect();
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("B 站弹幕验证", content.Content);
        Assert.Contains("/connect/connect.js", content.Content);
        Assert.Contains("default-src 'self'", controller.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", controller.Response.Headers["X-Frame-Options"].ToString());
    }

    [Fact]
    public async Task AccountPage_ReturnsHtml()
    {
        var controller = Create();
        var result = await controller.Account();
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("账号中心", content.Content);
        Assert.Contains("/account/account.js", content.Content);
    }

    [Fact]
    public void ConnectJs_UsesTextContent_AndNoInnerHtml()
    {
        var js = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "servers", "auth", "wwwroot", "connect", "connect.js"));
        Assert.Contains("textContent", js);
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("localStorage", js);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "servers", "Z.Vtuber.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class AuthLoginRequestsControllerTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;
    private readonly DanmakuHealthRegistry _registry;

    public AuthLoginRequestsControllerTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
        _registry = new DanmakuHealthRegistry(_services.Clock);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private LoginRequestsController CreateController(TestRateLimiter? limiter = null)
    {
        var controller = new LoginRequestsController(
            _services.LoginRequestService,
            limiter ?? _services.RateLimiter,
            _registry,
            AuthTestOptions.Opt(_services.DanmakuOptions),
            AuthTestOptions.Opt(_services.AuthOptions),
            _services.SessionService);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.UserAgent = "test-agent";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private void MarkRoomReady()
    {
        _registry.MarkLeader("30066");
        _registry.MarkConnected("30066");
        _registry.MarkHeartbeat("30066");
    }

    [Fact]
    public async Task Create_WhenNoRoomReady_Returns503()
    {
        var controller = CreateController();
        var ex = await Assert.ThrowsAsync<AuthException>(() => controller.Create(
            new CreateLoginRequestRequest { ClientId = "client", ReturnTo = AuthTestOptions.ReturnTo() }, default));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        Assert.Equal(AuthErrorCodes.DanmakuSourceUnavailable, ex.Code);
    }

    [Fact]
    public async Task CreateThenGet_WorksEndToEnd()
    {
        MarkRoomReady();
        var controller = CreateController();
        var result = await controller.Create(new CreateLoginRequestRequest
        {
            ClientId = "client",
            ReturnTo = AuthTestOptions.ReturnTo()
        }, default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<CreatedLoginRequestResponse>(created.Value);
        Assert.Equal(10, body.Token.Length);

        controller.HttpContext.Request.Headers["X-Poll-Token"] = body.PollToken;
        var getResult = await controller.Get(body.RequestId, default);
        var ok = Assert.IsType<OkObjectResult>(getResult);
        var status = Assert.IsType<LoginRequestStatusResponse>(ok.Value);
        Assert.Equal(LoginStatus.Pending, status.Status);
    }

    [Fact]
    public async Task Create_RateLimited_Returns429()
    {
        MarkRoomReady();
        var controller = CreateController(new TestRateLimiter { Allow = false });
        var ex = await Assert.ThrowsAsync<AuthException>(() => controller.Create(
            new CreateLoginRequestRequest { ClientId = "client", ReturnTo = AuthTestOptions.ReturnTo() }, default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public void Rooms_ReturnConfiguredSnapshotsWithoutSecrets()
    {
        _registry.MarkLeader("30066");
        _registry.MarkConnected("30066");
        _registry.MarkHeartbeat("30066");
        var controller = new DanmakuRoomsController(_registry, AuthTestOptions.Opt(_services.DanmakuOptions));
        var result = controller.Rooms();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RoomListResponse>(ok.Value);
        var room = Assert.Single(body.Rooms);
        Assert.Equal("30066", room.RoomId);
        Assert.True(room.Connected);
        Assert.True(room.Available);
        Assert.Equal(RoomRoles.Leader, room.Role);
    }
}

public sealed class AuthOAuthProviderTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;

    public AuthOAuthProviderTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private T WithContext<T>(T controller, Action<DefaultHttpContext>? configure = null) where T : ControllerBase
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        configure?.Invoke(context);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private OAuthAuthorizeController CreateAuthorize() => WithContext(new OAuthAuthorizeController(
        AuthTestOptions.Opt(_services.AuthOptions),
        AuthTestOptions.Opt(_services.ProviderOptions),
        _services.SessionService,
        _services.TicketService,
        _services.CodeService,
        _services.Users,
        _services.Clock,
        NullLogger<OAuthAuthorizeController>.Instance));

    private OAuthTokenController CreateToken() => WithContext(new OAuthTokenController(
        AuthTestOptions.Opt(_services.AuthOptions),
        AuthTestOptions.Opt(_services.ProviderOptions),
        _services.CodeService,
        _services.TokenService,
        _services.RateLimiter,
        _services.Users,
        NullLogger<OAuthTokenController>.Instance));

    [Fact]
    public async Task Authorize_WithoutSessionOrTicket_RedirectsToAccountCenterWithReturnTo()
    {
        var controller = CreateAuthorize();
        controller.HttpContext.Request.QueryString = new QueryString(BuildAuthorizeQuery());
        var result = await controller.Authorize(default);
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("/connect/bilibili?return_to=", redirect.Url);
    }

    [Fact]
    public async Task Authorize_InvalidClient_Returns400()
    {
        var controller = CreateAuthorize();
        controller.HttpContext.Request.QueryString = new QueryString(
            "?client_id=wrong&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback&response_type=code");
        var result = await controller.Authorize(default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TicketFlow_ProducesCodeThenTokensThenUserInfo()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(403496, "Zero_Ash", "https://i2.hdslb.com/face.jpg", "30066", null);
        var ticket = await _services.TicketService.CreateAsync(user.User.Id, AuthTestOptions.ReturnTo());

        var authorize = CreateAuthorize();
        authorize.HttpContext.Request.QueryString = new QueryString(BuildAuthorizeQuery(ticket.Ticket));
        var result = await authorize.Authorize(default);
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith(_services.ProviderOptions.RedirectUri + "?code=", redirect.Url);
        Assert.NotEmpty(authorize.Response.Headers.SetCookie.ToString());
        Assert.Equal("no-referrer", authorize.Response.Headers["Referrer-Policy"].ToString());

        var code = QueryHelpers.ParseQuery(new Uri(redirect.Url!).Query)["code"].ToString();
        var tokenController = CreateToken();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{_services.ProviderOptions.ClientId}:{_services.ProviderOptions.ClientSecret}"));
        tokenController.HttpContext.Request.Headers.Authorization = "Basic " + basic;
        var tokenResult = await tokenController.Token(
            "authorization_code", code, _services.ProviderOptions.RedirectUri, null, null, null, null, default);
        var tokenOk = Assert.IsType<OkObjectResult>(tokenResult);
        using var tokenJson = JsonDocument.Parse(JsonSerializer.Serialize(tokenOk.Value));
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
        var refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrEmpty(accessToken));
        Assert.False(string.IsNullOrEmpty(refreshToken));

        var userInfo = WithContext(new OAuthUserInfoController(
            AuthTestOptions.Opt(_services.AuthOptions), _services.TokenService, _services.Users),
            context => context.Request.Headers.Authorization = "Bearer " + accessToken);
        var infoResult = await userInfo.UserInfo(default);
        var infoOk = Assert.IsType<OkObjectResult>(infoResult);
        using var infoJson = JsonDocument.Parse(JsonSerializer.Serialize(infoOk.Value));
        Assert.Equal("bili_403496", infoJson.RootElement.GetProperty("sub").GetString());
        Assert.Equal("Zero_Ash", infoJson.RootElement.GetProperty("name").GetString());
        Assert.Equal("30066", infoJson.RootElement.GetProperty("bilibili_room_id").GetString());
    }

    [Fact]
    public async Task TicketReplay_IsRejectedWithoutCode()
    {
        var user = await _services.UserService.UpsertBilibiliUserAsync(42, "u", null, "30066", null);
        var ticket = await _services.TicketService.CreateAsync(user.User.Id, AuthTestOptions.ReturnTo());

        var first = CreateAuthorize();
        first.HttpContext.Request.QueryString = new QueryString(BuildAuthorizeQuery(ticket.Ticket));
        Assert.IsType<RedirectResult>(await first.Authorize(default));

        var replay = CreateAuthorize();
        replay.HttpContext.Request.QueryString = new QueryString(BuildAuthorizeQuery(ticket.Ticket));
        Assert.IsType<BadRequestObjectResult>(await replay.Authorize(default));
    }

    [Fact]
    public async Task TokenEndpoint_RejectsWrongClientSecret()
    {
        var controller = CreateToken();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("casdoor-danmaku:wrong"));
        controller.HttpContext.Request.Headers.Authorization = "Basic " + basic;
        var result = await controller.Token("authorization_code", "code", "redirect", null, null, null, null, default);
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_RejectsUnsupportedGrant()
    {
        var controller = CreateToken();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{_services.ProviderOptions.ClientId}:{_services.ProviderOptions.ClientSecret}"));
        controller.HttpContext.Request.Headers.Authorization = "Basic " + basic;
        var result = await controller.Token("password", null, null, null, null, null, null, default);
        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    private static string BuildAuthorizeQuery(string? ticket = null)
    {
        var query = "/oauth/authorize?client_id=casdoor-danmaku" +
                    "&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback" +
                    "&response_type=code&state=state-1&scope=openid%20profile";
        if (!string.IsNullOrEmpty(ticket))
        {
            query += "&ticket=" + Uri.EscapeDataString(ticket);
        }

        return query;
    }
}
