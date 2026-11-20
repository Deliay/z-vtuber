using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Z.Vtuber.Auth;
using Z.Vtuber.Auth.Contracts;

namespace Z.Vtuber.Tests.AuthFakes;

/// <summary>
/// 用 <see cref="AuthApp.Build"/> + 临时端口启动真实 Kestrel 的 auth HTTP 宿主。
/// Mongo/Redis 连接由调用方（Testcontainers 夹具）提供。
/// </summary>
public sealed class AuthApiFactory : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AuthApiFactory(WebApplication app, HttpClient client, string baseUrl)
    {
        _app = app;
        Client = client;
        BaseUrl = baseUrl;
    }

    public HttpClient Client { get; }

    public string BaseUrl { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<AuthApiFactory> StartAsync(
        string mongoConnectionString,
        string databaseName,
        string redisConnectionString,
        FakeDanmakuSource source,
        string mode = "casdoor",
        string[]? roomIds = null,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        roomIds ??= ["30066"];
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:mongo"] = mongoConnectionString,
            ["ConnectionStrings:messaging"] = redisConnectionString,
            ["Auth:Mode"] = mode,
            ["Auth:TokenPepper"] = AuthTestOptions.Pepper,
            ["Auth:PublicBaseUrl"] = "http://localhost:8087",
            ["Auth:AccountCenterUrl"] = "/connect/bilibili",
            ["Auth:ConfirmOperationTimeoutSeconds"] = "2",
            ["Auth:Danmaku:BaseUrl"] = source.BaseUrl,
            ["Auth:Danmaku:BackfillWindowSeconds"] = "600",
            ["Auth:Danmaku:NotReadyGraceSeconds"] = "3",
            ["Auth:Danmaku:ReadTimeoutSeconds"] = "10",
            ["Auth:Danmaku:LeaderLockTtlSeconds"] = "2",
            ["Auth:Danmaku:ReconnectMaxSeconds"] = "2",
            ["OAuthProvider:ClientId"] = AuthTestOptions.Provider().ClientId,
            ["OAuthProvider:ClientSecret"] = AuthTestOptions.Provider().ClientSecret,
            ["OAuthProvider:RedirectUri"] = AuthTestOptions.Provider().RedirectUri,
            ["OAuthProvider:AccessTokenSeconds"] = "3600",
            ["OAuthProvider:RefreshTokenDays"] = "30",
            ["OAuthProvider:AuthorizationCodeSeconds"] = "300",
            ["Mongo:DatabaseName"] = databaseName
        };

        for (var i = 0; i < roomIds.Length; i++)
        {
            values[$"Auth:Danmaku:RoomIds:{i}"] = roomIds[i];
        }

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }
        }

        var app = AuthApp.Build([], values);
        // auth 使用的数据库名来自 MongoDbInitializer.DatabaseName；测试库通过连接串 path 指定。
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Auth host did not bind an address.");

        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            BaseAddress = new Uri(address)
        };

        return new AuthApiFactory(app, client, address);
    }

    public async Task WaitForRoomAvailableAsync(TimeSpan? timeout = null, params string[] roomIds)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var rooms = await Client.GetFromJsonAsync<RoomListResponse>("/api/auth/danmaku/rooms");
            if (rooms is not null && roomIds.All(id => rooms.Rooms.Any(x => x.RoomId == id && x.Available)))
            {
                return;
            }

            await Task.Delay(50);
        }

        var snapshot = await Client.GetStringAsync("/api/auth/danmaku/rooms");
        throw new InvalidOperationException($"Rooms were not available in time: {snapshot}");
    }

    public async Task<RoomListResponse> GetRoomsAsync() =>
        await Client.GetFromJsonAsync<RoomListResponse>("/api/auth/danmaku/rooms")
        ?? throw new InvalidOperationException("Rooms response was empty.");

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await _app.DisposeAsync();
    }
}
