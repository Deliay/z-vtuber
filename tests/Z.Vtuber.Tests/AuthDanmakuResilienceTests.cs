using Z.Vtuber.Tests.AuthFakes;
using System.Net.Http.Json;

namespace Z.Vtuber.Tests;

/// <summary>
/// Phase 9.4：断线重连、补投与 follower 可用的弹性链路。
/// </summary>
public sealed class AuthDanmakuResilienceTests : IClassFixture<MongoDbFixture>, IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _mongo;
    private readonly RedisFixture _redis;
    private FakeDanmakuSource _source = null!;

    public AuthDanmakuResilienceTests(MongoDbFixture mongo, RedisFixture redis)
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

    private Task<AuthApiFactory> StartAsync() =>
        AuthApiFactory.StartAsync(
            _mongo.ConnectionString,
            _mongo.Database.DatabaseNamespace.DatabaseName,
            _redis.ConnectionString,
            _source,
            mode: "casdoor",
            roomIds: ["30066"]);

    [Fact]
    public async Task ReconnectsAfterSourceDrops()
    {
        await using var factory = await StartAsync();
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");
        Assert.Equal(1, _source.SubscriberCount("30066"));

        await _source.DisconnectAsync("30066");
        await TestWait.UntilAsync(
            async () => (await factory.GetRoomsAsync()).Rooms.Single().Connected == false,
            TimeSpan.FromSeconds(5),
            describe: "room disconnected after source drop");

        await TestWait.UntilAsync(
            async () => (await factory.GetRoomsAsync()).Rooms.Single().Connected,
            TimeSpan.FromSeconds(8),
            describe: "room reconnected with backoff");
    }

    [Fact]
    public async Task BackfillAfterReconnect_ProcessesTokenMissedWhileDisconnected()
    {
        await using var factory = await StartAsync();
        await factory.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");

        using var createResponse = await factory.Client.PostAsJsonAsync(
            "/api/auth/login-requests",
            new Z.Vtuber.Auth.Contracts.CreateLoginRequestRequest
            {
                ClientId = "account-center",
                ReturnTo = AuthTestOptions.ReturnTo()
            });
        var created = (await createResponse.Content.ReadFromJsonAsync<Z.Vtuber.Auth.Contracts.CreatedLoginRequestResponse>())!;

        await _source.DisconnectAsync("30066");
        // 断线期间用户发送 token：只进入历史，不进入实时通道。
        await _source.PublishHistoryOnlyAsync("30066", created.Token);

        var verified = await AuthDanmakuEndToEndTests.WaitForStatusAsync(factory, created, "verified");
        Assert.Equal("30066", verified.SourceRoomId);
    }

    [Fact]
    public async Task FollowerInstance_IsAvailableWhileLeaderHoldsLease()
    {
        await using var leader = await StartAsync();
        await leader.WaitForRoomAvailableAsync(TimeSpan.FromSeconds(10), "30066");

        await using var follower = await StartAsync();
        await TestWait.UntilAsync(async () =>
        {
            var room = (await follower.GetRoomsAsync()).Rooms.Single();
            return room.Role == "standby" && room.Available;
        }, TimeSpan.FromSeconds(8), describe: "follower sees leader lease and is available");
    }
}
