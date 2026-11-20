using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Tests;

public sealed class AuthDanmakuEventHandlerTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;

    public AuthDanmakuEventHandlerTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DanmakuEventHandler CreateHandler(TestRateLimiter? limiter = null) => new(
        _services.LoginRequests,
        _services.Hasher,
        limiter ?? _services.RateLimiter,
        AuthTestOptions.Opt(_services.DanmakuOptions),
        AuthTestOptions.Opt(_services.AuthOptions),
        _services.Audit,
        _services.Clock,
        NullLogger<DanmakuEventHandler>.Instance);

    [Fact]
    public async Task ValidToken_MarksRequestVerified()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await CreateHandler().HandleAsync("30066", AuthTestData.Event("A1b2C3d4E5"), default);

        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Verified, stored!.Status);
        Assert.Equal("30066", stored.SourceRoomId);
        Assert.Equal(403496, stored.SenderUid);
        Assert.Equal("Zero_Ash", stored.SenderName);
        Assert.NotNull(stored.SenderAvatar);
    }

    [Fact]
    public async Task UnknownToken_IsIgnored()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await CreateHandler().HandleAsync("30066", AuthTestData.Event("Z9y8X7w6V5"), default);
        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Pending, stored!.Status);
    }

    [Fact]
    public async Task NonTenCharText_IsIgnoredWithoutLookup()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await CreateHandler().HandleAsync("30066", AuthTestData.Event("hello"), default);
        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Pending, stored!.Status);
    }

    [Fact]
    public async Task UnconfiguredRoom_IsIgnored()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        await CreateHandler().HandleAsync("99999", AuthTestData.Event("A1b2C3d4E5", roomId: "99999"), default);
        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Pending, stored!.Status);
    }

    [Fact]
    public async Task RateLimitedUid_IsDropped()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        var limiter = new TestRateLimiter { Allow = false };
        await CreateHandler(limiter).HandleAsync("30066", AuthTestData.Event("A1b2C3d4E5"), default);
        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(LoginStatus.Pending, stored!.Status);
    }

    [Fact]
    public async Task SecondEvent_DoesNotOverwriteVerifiedSender()
    {
        var record = await _services.InsertPendingAsync("A1b2C3d4E5");
        var handler = CreateHandler();
        await handler.HandleAsync("30066", AuthTestData.Event("A1b2C3d4E5"), default);
        await handler.HandleAsync("30066", AuthTestData.Event("A1b2C3d4E5", uid: 999, name: "other"), default);

        var stored = await _services.LoginRequests.FindByRequestIdAsync(record.RequestId);
        Assert.Equal(403496, stored!.SenderUid);
    }
}
