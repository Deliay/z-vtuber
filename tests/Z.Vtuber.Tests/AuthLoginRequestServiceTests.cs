using Z.Vtuber.Auth.Persistence;
using MongoDB.Driver;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Tests;

public sealed class AuthLoginRequestServiceTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;

    public AuthLoginRequestServiceTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_ReturnsTokenOnceAndStoresOnlyHashes()
    {
        var created = await _services.LoginRequestService.CreateAsync(
            "account-center", "nonce", AuthTestOptions.ReturnTo(), "127.0.0.1", "test", default);

        Assert.Equal(10, created.Token.Length);
        Assert.StartsWith("p_", created.PollToken);
        Assert.Single(created.RoomIds);
        Assert.Equal("30066", created.RoomIds[0]);

        var record = await _services.LoginRequests.FindByRequestIdAsync(created.RequestId);
        Assert.NotNull(record);
        Assert.Equal(LoginStatus.Pending, record!.Status);
        Assert.NotEqual(created.Token, record.TokenHash);
        Assert.NotEqual(created.PollToken, record.PollTokenHash);
        Assert.True(_services.Hasher.Verify(created.Token, record.TokenHash));
    }

    [Fact]
    public async Task Create_InCasdoorMode_RequiresValidReturnTo()
    {
        await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.CreateAsync("client", null, null, "127.0.0.1", null, default));
        await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.CreateAsync("client", null, "https://evil.example/oauth/authorize", "127.0.0.1", null, default));
    }

    [Fact]
    public async Task Create_InStandaloneMode_DoesNotRequireReturnTo()
    {
        var services = AuthTestServices.Create(_fixture.Database, mode: AuthModes.Standalone);
        var created = await services.LoginRequestService.CreateAsync(
            "client", null, null, "127.0.0.1", null, default);
        Assert.Equal(10, created.Token.Length);
    }

    [Fact]
    public async Task Get_RequiresMatchingPollToken()
    {
        var created = await _services.LoginRequestService.CreateAsync(
            "client", null, AuthTestOptions.ReturnTo(), null, null, default);

        await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.GetAsync(created.RequestId, "p_wrong", default));

        var (_, view) = await _services.LoginRequestService.GetAsync(created.RequestId, created.PollToken, default);
        Assert.Equal(LoginStatus.Pending, view.Status);
    }

    [Fact]
    public async Task Get_ComputesExpiredFromServerClock()
    {
        var created = await _services.LoginRequestService.CreateAsync(
            "client", null, AuthTestOptions.ReturnTo(), null, null, default);
        _services.Clock.Advance(TimeSpan.FromSeconds(301));

        var (_, view) = await _services.LoginRequestService.GetAsync(created.RequestId, created.PollToken, default);
        Assert.Equal(LoginStatus.Expired, view.Status);

        var ex = await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.ConfirmAsync(created.RequestId, created.PollToken, null, null, default));
        Assert.Equal(AuthErrorCodes.LoginRequestExpired, ex.Code);
    }

    [Fact]
    public async Task Confirm_PendingRequest_IsRejected()
    {
        var created = await _services.LoginRequestService.CreateAsync(
            "client", null, AuthTestOptions.ReturnTo(), null, null, default);
        var ex = await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.ConfirmAsync(created.RequestId, created.PollToken, null, null, default));
        Assert.Equal(AuthErrorCodes.LoginRequestNotVerified, ex.Code);
    }

    [Fact]
    public async Task Confirm_AfterDanmakuVerification_BindsUserAndIssuesTicket()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        var verified = await _services.LoginRequests.TryMarkVerifiedAsync(
            record.TokenHash, "30066",
            new DanmakuSender("A1b2C3d4E5", 403496, "Zero_Ash", "https://i2.hdslb.com/face.jpg", string.Empty),
            _services.Clock.GetUtcNow().UtcDateTime, 120);
        Assert.NotNull(verified);

        var result = await _services.LoginRequestService.ConfirmAsync(
            record.RequestId, "p_test_poll_token", "127.0.0.1", "ua", default);

        Assert.Equal(AuthModes.Casdoor, result.Mode);
        Assert.Equal("Zero_Ash", result.User.Name);
        Assert.Contains("ticket=", result.RedirectUrl);
        Assert.Contains("client_id=casdoor-danmaku", result.RedirectUrl);

        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Consumed, stored!.Status);
        Assert.NotNull(stored.ConsumedAt);

        var user = await _services.Users.FindByPlatformUidAsync("bilibili", 403496);
        Assert.NotNull(user);
        Assert.Equal("bili_403496", user!.ProviderSubject);
        Assert.Equal(1, user.LoginCount);
    }

    [Fact]
    public async Task Confirm_Twice_SecondIsAlreadyConsumed()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await _services.LoginRequests.TryMarkVerifiedAsync(
            record.TokenHash, "30066",
            new DanmakuSender("A1b2C3d4E5", 403496, "Zero_Ash", null, string.Empty),
            _services.Clock.GetUtcNow().UtcDateTime, 120);

        await _services.LoginRequestService.ConfirmAsync(record.RequestId, "p_test_poll_token", null, null, default);
        var ex = await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.ConfirmAsync(record.RequestId, "p_test_poll_token", null, null, default));
        Assert.Equal(AuthErrorCodes.LoginRequestAlreadyConsumed, ex.Code);
    }

    [Fact]
    public async Task Confirm_WhenUserDisabled_RevertsAndRejects()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await _services.LoginRequests.TryMarkVerifiedAsync(
            record.TokenHash, "30066",
            new DanmakuSender("A1b2C3d4E5", 403496, "Zero_Ash", null, string.Empty),
            _services.Clock.GetUtcNow().UtcDateTime, 120);

        var user = await _services.UserService.UpsertBilibiliUserAsync(403496, "Zero_Ash", null, "30066", null);
        await _fixture.Database.GetCollection<AuthUserRecord>(AuthCollectionNames.Users)
            .UpdateOneAsync(x => x.Id == user.User.Id, MongoDB.Driver.Builders<AuthUserRecord>.Update.Set(x => x.Status, AuthUserStatus.Disabled));

        var ex = await Assert.ThrowsAsync<AuthException>(() =>
            _services.LoginRequestService.ConfirmAsync(record.RequestId, "p_test_poll_token", null, null, default));
        Assert.Equal(AuthErrorCodes.UserDisabled, ex.Code);

        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Verified, stored!.Status);
    }

    [Fact]
    public async Task Cancel_IsIdempotent_AndConsumedCannotBeCancelled()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await _services.LoginRequestService.CancelAsync(record.RequestId, "p_test_poll_token", null, default);
        await _services.LoginRequestService.CancelAsync(record.RequestId, "p_test_poll_token", null, default);

        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Cancelled, stored!.Status);
    }

    [Fact]
    public async Task Scanner_MigratesTimedOutStatusesToExpired()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        _services.Clock.Advance(TimeSpan.FromSeconds(301));

        var scanner = new LoginRequestScanner(
            _services.LoginRequests, _services.Audit, _services.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LoginRequestScanner>.Instance);
        await scanner.ScanOnceAsync();

        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Expired, stored!.Status);
    }

    [Fact]
    public async Task ConcurrentConfirm_OnlyOneSucceeds()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await _services.LoginRequests.TryMarkVerifiedAsync(
            record.TokenHash, "30066",
            new DanmakuSender("A1b2C3d4E5", 403496, "Zero_Ash", null, string.Empty),
            _services.Clock.GetUtcNow().UtcDateTime, 120);

        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try
            {
                await _services.LoginRequestService.ConfirmAsync(
                    record.RequestId, "p_test_poll_token", null, null, default);
                return true;
            }
            catch (AuthException)
            {
                return false;
            }
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Single(results.Where(x => x));
    }
}
