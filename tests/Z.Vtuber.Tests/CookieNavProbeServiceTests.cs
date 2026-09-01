using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using MongoDB.Bson;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public sealed class CookieNavProbeServiceTests
{
    private sealed class MockBiliApiClient : IBiliApiClient
    {
        public Func<string, CancellationToken, Task<NavInfo>> NavInfoFunc { get; set; } =
            (_, _) => Task.FromResult(new NavInfo { Mid = 123 });

        public Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default) =>
            NavInfoFunc(cookie, ct);

        public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
            => Task.FromResult(new LiveRoomInfo { Title = "test", RoomId = roomId, LiveStatus = 0 });

        public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
            => Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = "", Items = [] });

        public Task<System.Text.Json.JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
            => Task.FromResult(System.Text.Json.JsonDocument.Parse("""{"mid":0}""").RootElement.Clone());
    }

    private static readonly BilibiliCrawlerOptions Options = new() { CookieProbeIntervalMinutes = 5, CooldownMinutes = 30 };

    private static CookieNavProbeService CreateService(
        IBiliApiClient bili,
        ICookiePoolService pool,
        ICookieAccountInfoRepository repo) =>
        new(bili, pool, repo, Options, NullLogger<CookieNavProbeService>.Instance);

    private static CookieRecord Record() => new()
    {
        Id = ObjectId.GenerateNewId(),
        Cookie = "cookie1",
        Status = CookieStatus.Active,
    };

    [Fact]
    public async Task ProbeAsync_Success_UpsertsAccountInfoAndReturnsSuccess()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => Task.FromResult(new NavInfo { Mid = 999, Name = "昵称" })
        };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository();
        var record = Record();

        var outcome = await CreateService(bili, pool, repo).ProbeAsync(record);

        Assert.Equal(CookieProbeOutcome.Success, outcome);
        Assert.Empty(pool.OfflineCalls);
        Assert.Empty(pool.CooldownCalls);
        var upsert = Assert.Single(repo.Upserts);
        Assert.Equal(record.Id, upsert.CookieId);
        Assert.Equal(999, upsert.Mid);
        Assert.Equal("昵称", upsert.Uname);
        Assert.NotEqual(default, upsert.FetchedAt);
    }

    [Fact]
    public async Task ProbeAsync_MidZero_MarksOfflineAndNoUpsert()
    {
        var bili = new MockBiliApiClient { NavInfoFunc = (_, _) => Task.FromResult(new NavInfo { Mid = 0 }) };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository();

        var outcome = await CreateService(bili, pool, repo).ProbeAsync(Record());

        Assert.Equal(CookieProbeOutcome.Offline, outcome);
        Assert.Equal("cookie1", Assert.Single(pool.OfflineCalls));
        Assert.Empty(pool.CooldownCalls);
        Assert.Empty(repo.Upserts);
    }

    [Fact]
    public async Task ProbeAsync_GenericException_MarksOffline()
    {
        var bili = new MockBiliApiClient { NavInfoFunc = (_, _) => throw new InvalidOperationException("boom") };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository();

        var outcome = await CreateService(bili, pool, repo).ProbeAsync(Record());

        Assert.Equal(CookieProbeOutcome.Offline, outcome);
        Assert.Equal("cookie1", Assert.Single(pool.OfflineCalls));
        Assert.Empty(repo.Upserts);
    }

    [Fact]
    public async Task ProbeAsync_RiskControl_MarksCooldown()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => throw new RiskControlException("risk", "http://test", "cookie1")
        };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository();

        var outcome = await CreateService(bili, pool, repo).ProbeAsync(Record());

        Assert.Equal(CookieProbeOutcome.Cooldown, outcome);
        Assert.Empty(pool.OfflineCalls);
        var call = Assert.Single(pool.CooldownCalls);
        Assert.Equal("cookie1", call.Cookie);
        Assert.Equal(TimeSpan.FromMinutes(30), call.Duration);
    }

    [Fact]
    public async Task ProbeAsync_Backoff_ReturnsBackoffWithoutStateChange()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => throw new CrawlBackoffActiveException("backoff", TimeSpan.FromSeconds(10))
        };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository();

        var outcome = await CreateService(bili, pool, repo).ProbeAsync(Record());

        Assert.Equal(CookieProbeOutcome.Backoff, outcome);
        Assert.Empty(pool.OfflineCalls);
        Assert.Empty(pool.CooldownCalls);
    }

    [Fact]
    public async Task ProbeAsync_PersistFailure_StillReturnsSuccessWithoutStateChange()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => Task.FromResult(new NavInfo { Mid = 123, Name = "昵称" })
        };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository { ThrowOnUpsert = true };

        var outcome = await CreateService(bili, pool, repo).ProbeAsync(Record());

        Assert.Equal(CookieProbeOutcome.Success, outcome);
        Assert.Empty(pool.OfflineCalls);
        Assert.Empty(pool.CooldownCalls);
        Assert.Empty(repo.Upserts);
    }

    [Fact]
    public async Task ProbeAsync_Cancellation_Propagates()
    {
        var bili = new MockBiliApiClient { NavInfoFunc = (_, ct) => Task.FromException<NavInfo>(new OperationCanceledException(ct)) };
        var pool = new MockCookiePoolService();
        var repo = new MockCookieAccountInfoRepository();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(bili, pool, repo).ProbeAsync(Record()));
    }
}