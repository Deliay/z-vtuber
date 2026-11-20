using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.OAuth;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Tests;

internal class AuthTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public void Set(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}

internal static class AuthTestOptions
{
    public const string Pepper = "test-pepper-0123456789abcdef0123456789abcdef";

    public static AuthOptions Auth(string mode = AuthModes.Casdoor) => new()
    {
        Mode = mode,
        PublicBaseUrl = "http://localhost:8087",
        AccountCenterUrl = "/connect/bilibili",
        TokenPepper = Pepper,
        LoginTokenTtlSeconds = 300,
        ConfirmWindowSeconds = 120,
        ConfirmOperationTimeoutSeconds = 30,
        ProviderTicketSeconds = 60,
        ProviderSessionDays = 30,
        AllowDegradedRedis = true
    };

    public static OAuthProviderOptions Provider() => new()
    {
        ClientId = "casdoor-danmaku",
        ClientSecret = "test-provider-secret",
        RedirectUri = "https://casdoor.example.com/callback",
        AccessTokenSeconds = 3600,
        RefreshTokenDays = 30,
        AuthorizationCodeSeconds = 300
    };

    public static DanmakuOptions Danmaku(params string[] rooms) => new()
    {
        RoomIds = rooms.Length == 0 ? ["30066"] : rooms,
        BaseUrl = "http://localhost:5099",
        BackfillWindowSeconds = 600,
        NotReadyGraceSeconds = 30,
        ReadTimeoutSeconds = 60,
        MaxBackfillPages = 3,
        LeaderLockTtlSeconds = 15,
        ReconnectMaxSeconds = 30
    };

    public static IOptions<T> Opt<T>(T value) where T : class => Options.Create(value);

    public static string ReturnTo() =>
        "/oauth/authorize?client_id=casdoor-danmaku" +
        "&redirect_uri=https%3A%2F%2Fcasdoor.example.com%2Fcallback" +
        "&response_type=code&state=state-1&scope=openid%20profile";
}

internal sealed class TestRateLimiter : IAuthRateLimiter
{
    public bool Allow { get; set; } = true;

    public int Calls { get; private set; }

    public Task<bool> TryTokenPerMinuteAsync(string clientIp, CancellationToken ct) => Result();

    public Task<bool> TryTokenPerDayAsync(string clientIp, CancellationToken ct) => Result();

    public Task<bool> TryTokenPerClientAsync(string clientId, CancellationToken ct) => Result();

    public Task<bool> TryDanmakuUidAsync(long uid, CancellationToken ct) => Result();

    public Task<bool> TryOAuthTokenAsync(string clientId, string clientIp, CancellationToken ct) => Result();

    private Task<bool> Result()
    {
        Calls++;
        return Task.FromResult(Allow);
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(responder(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class AuthTestServices
{
    public required IMongoDatabase Database { get; init; }
    public required AuthTestClock Clock { get; init; }
    public required AuthOptions AuthOptions { get; init; }
    public required OAuthProviderOptions ProviderOptions { get; init; }
    public required DanmakuOptions DanmakuOptions { get; init; }
    public required TokenHasher Hasher { get; init; }
    public required LoginRequestRepository LoginRequests { get; init; }
    public required UserRepository Users { get; init; }
    public required ProviderSessionRepository ProviderSessions { get; init; }
    public required ProviderTicketRepository Tickets { get; init; }
    public required OAuthCodeRepository Codes { get; init; }
    public required ProviderTokenRepository Tokens { get; init; }
    public required StandaloneSessionRepository StandaloneSessions { get; init; }
    public required UserService UserService { get; init; }
    public required ProviderTicketService TicketService { get; init; }
    public required ProviderSessionService SessionService { get; init; }
    public required AuthorizationCodeService CodeService { get; init; }
    public required ProviderTokenService TokenService { get; init; }
    public required StandaloneSessionService StandaloneService { get; init; }
    public required LoginRequestService LoginRequestService { get; init; }
    public required AuditLogger Audit { get; init; }
    public required TestRateLimiter RateLimiter { get; init; }

    public static AuthTestServices Create(
        IMongoDatabase database,
        AuthTestClock? clock = null,
        string mode = AuthModes.Casdoor,
        DanmakuOptions? danmaku = null)
    {
        clock ??= new AuthTestClock();
        var authOptions = AuthTestOptions.Auth(mode);
        var provider = AuthTestOptions.Provider();
        var danmakuOptions = danmaku ?? AuthTestOptions.Danmaku();
        var hasher = new TokenHasher(authOptions.TokenPepper);

        var loginRequests = new LoginRequestRepository(database);
        var users = new UserRepository(database);
        var providerSessions = new ProviderSessionRepository(database);
        var tickets = new ProviderTicketRepository(database);
        var codes = new OAuthCodeRepository(database);
        var tokens = new ProviderTokenRepository(database);
        var standaloneSessions = new StandaloneSessionRepository(database);
        var auditRepo = new AuditRepository(database);
        var audit = new AuditLogger(auditRepo, clock, NullLogger<AuditLogger>.Instance);
        var userService = new UserService(users, clock, NullLogger<UserService>.Instance);
        var ticketService = new ProviderTicketService(
            tickets, hasher, AuthTestOptions.Opt(authOptions), AuthTestOptions.Opt(provider), clock,
            NullLogger<ProviderTicketService>.Instance);
        var sessionService = new ProviderSessionService(
            providerSessions, users, hasher, AuthTestOptions.Opt(authOptions), clock,
            NullLogger<ProviderSessionService>.Instance);
        var codeService = new AuthorizationCodeService(
            codes, hasher, AuthTestOptions.Opt(provider), clock, NullLogger<AuthorizationCodeService>.Instance);
        var tokenService = new ProviderTokenService(
            tokens, hasher, AuthTestOptions.Opt(provider), clock, NullLogger<ProviderTokenService>.Instance);
        var standalone = new StandaloneSessionService(
            standaloneSessions, users, hasher, AuthTestOptions.Opt(authOptions), clock,
            NullLogger<StandaloneSessionService>.Instance);
        var login = new LoginRequestService(
            loginRequests, hasher, userService, ticketService, standalone,
            AuthTestOptions.Opt(authOptions), AuthTestOptions.Opt(provider), AuthTestOptions.Opt(danmakuOptions),
            audit, clock, NullLogger<LoginRequestService>.Instance);

        return new AuthTestServices
        {
            Database = database,
            Clock = clock,
            AuthOptions = authOptions,
            ProviderOptions = provider,
            DanmakuOptions = danmakuOptions,
            Hasher = hasher,
            LoginRequests = loginRequests,
            Users = users,
            ProviderSessions = providerSessions,
            Tickets = tickets,
            Codes = codes,
            Tokens = tokens,
            StandaloneSessions = standaloneSessions,
            UserService = userService,
            TicketService = ticketService,
            SessionService = sessionService,
            CodeService = codeService,
            TokenService = tokenService,
            StandaloneService = standalone,
            LoginRequestService = login,
            Audit = audit,
            RateLimiter = new TestRateLimiter()
        };
    }

    public async Task<AuthLoginRequestRecord> InsertPendingAsync(
        string token = "A1b2C3d4E5",
        string? returnTo = null,
        DateTime? expiresAt = null,
        string pollToken = "p_test_poll_token")
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var record = new AuthLoginRequestRecord
        {
            RequestId = TokenGenerator.CreateRequestId(),
            TokenHash = Hasher.Hash(token),
            PollTokenHash = Hasher.Hash(pollToken),
            ReturnTo = returnTo ?? AuthTestOptions.ReturnTo(),
            Status = LoginStatus.Pending,
            ExpiresAt = expiresAt ?? now.AddSeconds(300),
            CreatedAt = now,
            ClientId = "account-center"
        };
        await LoginRequests.InsertAsync(record);
        return record;
    }
}

internal static class AuthTestData
{
    public static string DanmakuPayload(
        string token,
        long uid = 403496,
        string name = "Zero_Ash",
        string? face = "https://i2.hdslb.com/bfs/face/test.jpg",
        string? originFace = null)
    {
        var baseUser = face is null
            ? new Dictionary<string, object?>
            {
                ["origin_info"] = new Dictionary<string, object?> { ["face"] = originFace }
            }
            : new Dictionary<string, object?> { ["face"] = face };

        var meta = new object?[16];
        meta[15] = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["base"] = baseUser
            }
        };

        var info = new object?[]
        {
            meta,
            token,
            new object?[] { uid, name },
            Array.Empty<object?>()
        };

        return JsonSerializer.Serialize(new { cmd = "DANMU_MSG", info });
    }

    public static JsonElement Payload(string token, long uid = 403496, string name = "Zero_Ash", string? face = "https://i2.hdslb.com/bfs/face/test.jpg") =>
        JsonDocument.Parse(DanmakuPayload(token, uid, name, face)).RootElement.Clone();

    public static DanmakuEvent Event(string token, string roomId = "30066", long uid = 403496, string? name = null) =>
        new(roomId, "danmaku", DateTime.UtcNow, Payload(token, uid, name ?? "Zero_Ash"));
}
