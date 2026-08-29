using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class CookiePoolServiceTests(MongoDbFixture fixture) : IAsyncLifetime
{
    private CookiePoolService CreateService(IServiceProvider? provider = null) =>
        new(fixture.Database, provider ?? new ProbeServiceProvider());

    private static readonly BilibiliCrawlerOptions Options = new() { CookieProbeIntervalMinutes = 5, CooldownMinutes = 30 };

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Next_RoundRobinsAcrossActiveCookies()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");

        var first = await service.NextAsync();
        var second = await service.NextAsync();
        var third = await service.NextAsync();

        Assert.Equal("cookie-a", first.Cookie);
        Assert.Equal("cookie-b", second.Cookie);
        Assert.Equal("cookie-a", third.Cookie);
    }

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
    public async Task OfflineCookie_IsNotSelected()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");
        await service.MarkOfflineAsync("cookie-a");

        var next = await service.NextAsync();
        Assert.Equal("cookie-b", next.Cookie);
    }

    [Fact]
    public async Task NoActiveCookie_ThrowsObservableException()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<NoAvailableCookieException>(() => service.NextAsync());
    }

    [Fact]
    public async Task CooldownCookie_IsNotAssignedByNext()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");
        await service.MarkCooldownAsync("cookie-a", TimeSpan.FromMinutes(30));

        var next = await service.NextAsync();
        Assert.Equal("cookie-b", next.Cookie);
    }

    [Fact]
    public async Task CooldownCookie_BecomesAvailableAfterExpiry()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");

        // cooldown 1s, delay 2s to avoid BSON 毫秒精度 + 调度抖动竞态
        await service.MarkCooldownAsync("cookie-a", TimeSpan.FromSeconds(1));
        var during = await service.NextAsync();
        Assert.Equal("cookie-b", during.Cookie);

        await Task.Delay(TimeSpan.FromSeconds(2));
        var after = await service.NextAsync();
        Assert.Equal("cookie-a", after.Cookie);
    }

    [Fact]
    public async Task LegacyDocument_WithoutCooldownUntil_IsIncludedInRotation()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");

        // Simulate legacy document: directly set CooldownUntil to null via raw update
        var collection = fixture.Database.GetCollection<MongoDB.Bson.BsonDocument>(
            Persistence.MongoCollectionNames.Cookies);
        var filter = new MongoDB.Bson.BsonDocument("cookie", "cookie-a");
        var update = new MongoDB.Bson.BsonDocument("$set",
            new MongoDB.Bson.BsonDocument("cooldownUntil",
                MongoDB.Bson.BsonNull.Value));
        await collection.UpdateOneAsync(filter, update);

        var next = await service.NextAsync();
        Assert.Equal("cookie-a", next.Cookie);
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

        public Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
        {
            NavCallCount++;
            return NavFunc(cookie, ct);
        }

        public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
            => Task.FromResult(new LiveRoomInfo { Title = "test", RoomId = roomId, LiveStatus = 0 });

        public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
            => Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = "", Items = [] });

        public Task<System.Text.Json.JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
            => Task.FromResult(System.Text.Json.JsonDocument.Parse("""{"mid":0}""").RootElement.Clone());
    }

    private (CookiePoolService Pool, NavBiliApiClient Bili, MockCookieAccountInfoRepository Repo) CreateProbingPool(
        NavBiliApiClient? bili = null)
    {
        var provider = new ProbeServiceProvider();
        var pool = new CookiePoolService(fixture.Database, provider);
        var resolvedBili = bili ?? new NavBiliApiClient();
        var repo = new MockCookieAccountInfoRepository();
        provider.Probe = new CookieNavProbeService(
            resolvedBili, pool, repo, Options, NullLogger<CookieNavProbeService>.Instance);
        return (pool, resolvedBili, repo);
    }

    [Fact]
    public async Task Next_FirstUse_ProbesExactlyOnce_WithAccountInfo_Persisted()
    {
        var (pool, bili, repo) = CreateProbingPool();
        await pool.AddAsync("cookie-a");

        var first = await pool.NextAsync();
        var second = await pool.NextAsync();

        Assert.Equal("cookie-a", first.Cookie);
        Assert.Equal("cookie-a", second.Cookie);
        Assert.Equal(1, bili.NavCallCount);
        var upsert = Assert.Single(repo.Upserts);
        Assert.Equal(first.Id, upsert.CookieId);
        Assert.Equal(1, upsert.Mid);
    }

    [Fact]
    public async Task Next_AllFirstUseFail_MarksOfflineAndThrowsNoAvailableCookie()
    {
        var bili = new NavBiliApiClient
        {
            NavFunc = (_, _) => throw new InvalidOperationException("nav down")
        };
        var (pool, _, _) = CreateProbingPool(bili);
        await pool.AddAsync("cookie-a");
        await pool.AddAsync("cookie-b");

        await Assert.ThrowsAsync<NoAvailableCookieException>(() => pool.NextAsync());

        Assert.Empty(await pool.GetActiveAsync());
    }

    [Fact]
    public async Task Next_SomeFirstUseFail_ContinuesToNextUsableCookie()
    {
        var bili = new NavBiliApiClient
        {
            NavFunc = (cookie, _) => cookie == "cookie-a"
                ? Task.FromException<NavInfo>(new InvalidOperationException("nav down"))
                : Task.FromResult(new NavInfo { Mid = 2 })
        };
        var (pool, _, _) = CreateProbingPool(bili);
        await pool.AddAsync("cookie-a");
        await pool.AddAsync("cookie-b");

        var next = await pool.NextAsync();

        Assert.Equal("cookie-b", next.Cookie);
        Assert.Equal("cookie-b", Assert.Single(await pool.GetActiveAsync()).Cookie);
    }

    [Fact]
    public async Task Next_FirstUseRiskControl_MarksCooldownAndContinues()
    {
        var bili = new NavBiliApiClient
        {
            NavFunc = (cookie, _) => cookie == "cookie-a"
                ? Task.FromException<NavInfo>(new RiskControlException("risk", "http://test", "cookie-a"))
                : Task.FromResult(new NavInfo { Mid = 2 })
        };
        var (pool, _, _) = CreateProbingPool(bili);
        await pool.AddAsync("cookie-a");
        await pool.AddAsync("cookie-b");

        var next = await pool.NextAsync();

        Assert.Equal("cookie-b", next.Cookie);
        // cookie-a 处于 cooldown，不在活跃列表中
        Assert.Equal("cookie-b", Assert.Single(await pool.GetActiveAsync()).Cookie);
    }

    [Fact]
    public async Task Next_FirstUseSuccessButPersistFails_StillReturnsCookie()
    {
        var (pool, _, repo) = CreateProbingPool();
        repo.ThrowOnUpsert = true;
        await pool.AddAsync("cookie-a");

        var next = await pool.NextAsync();

        Assert.Equal("cookie-a", next.Cookie);
        Assert.Empty(repo.Upserts);
    }
}
