using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public class CookiePoolServiceTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private CookiePoolService CreateService() =>
        new(fixture.Database);

    private static readonly BilibiliCrawlerOptions Options = new() { CookieProbeIntervalMinutes = 5, CooldownMinutes = 30 };

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddAsync_IsIdempotent()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-a");

        var active = await service.GetActiveAsync();
        Assert.Single(active);
    }

    [Fact]
    public async Task AddAsync_ReturnsRecordWithId_AndPreservesSameIdOnDuplicate()
    {
        var service = CreateService();
        var first = await service.AddAsync("cookie-a");
        var second = await service.AddAsync("cookie-a");

        Assert.NotEqual(MongoDB.Bson.ObjectId.Empty, first.Id);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task MarkOfflineAsync_AlreadyOffline_ReturnsFalse()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        var first = await service.MarkOfflineAsync("cookie-a");
        Assert.True(first);

        var second = await service.MarkOfflineAsync("cookie-a");
        Assert.False(second);
    }

    [Fact]
    public async Task MarkOfflineAsync_NonExistingCookie_ReturnsFalse()
    {
        var service = CreateService();
        var result = await service.MarkOfflineAsync("non-existing");
        Assert.False(result);
    }

    [Fact]
    public async Task GetActiveAsync_ExcludesCooldownCookies()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");
        await service.MarkCooldownAsync("cookie-a", TimeSpan.FromMinutes(30));

        var active = await service.GetActiveAsync();
        Assert.Single(active);
        Assert.Equal("cookie-b", active[0].Cookie);
    }

    private sealed class NavBiliApiClient : IBiliApiClient
    {
        public Func<string, CancellationToken, Task<NavInfo>> NavFunc { get; set; } =
            (_, _) => Task.FromResult(new NavInfo { Mid = 1 });
        public int NavCallCount { get; private set; }

        public Task<NavInfo> GetNavInfoV2Async(string cookieId, string cookie, CancellationToken ct = default)
        {
            NavCallCount++;
            return NavFunc(cookie, ct);
        }

        public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookieId, string cookie, long roomId, CancellationToken ct = default)
            => Task.FromResult(new LiveRoomInfo { Title = "test", RoomId = roomId, LiveStatus = 0 });

        public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookieId, string cookie, long mid, string offset, CancellationToken ct = default)
            => Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = "", Items = [] });

        public Task<System.Text.Json.JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
            => Task.FromResult(System.Text.Json.JsonDocument.Parse("""{"mid":0}""").RootElement.Clone());

        public Task<GuardInfo> GetRoomGuardListAsync(string cookieId, string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default)
            => Task.FromResult(new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] });
    }

    [Fact]
    public async Task FindByCookieAsync_ExistingCookie_ReturnsRecord()
    {
        var pool = new CookiePoolService(fixture.Database);
        await pool.AddAsync("test-cookie");

        var result = await pool.FindByCookieAsync("test-cookie");

        Assert.NotNull(result);
        Assert.Equal("test-cookie", result!.Cookie);
    }

    [Fact]
    public async Task FindByCookieAsync_NonExistingCookie_ReturnsNull()
    {
        var pool = new CookiePoolService(fixture.Database);

        var result = await pool.FindByCookieAsync("non-existing");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindByCookieAsync_OfflineCookie_ReturnsRecord()
    {
        var pool = new CookiePoolService(fixture.Database);
        await pool.AddAsync("offline-cookie");
        await pool.MarkOfflineAsync("offline-cookie");

        var result = await pool.FindByCookieAsync("offline-cookie");

        Assert.NotNull(result);
        Assert.Equal("offline-cookie", result!.Cookie);
    }
}
