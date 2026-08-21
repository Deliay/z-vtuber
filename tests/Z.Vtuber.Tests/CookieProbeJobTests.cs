using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using MongoDB.Bson;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Jobs;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

public sealed class CookieProbeJobTests
{
    private sealed class MockBiliApiClient : IBiliApiClient
    {
        public Func<string, CancellationToken, Task<NavInfo>>? NavInfoFunc { get; set; }
        private int _callCount;
        public int CallCount => _callCount;

        public Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            return NavInfoFunc?.Invoke(cookie, ct)
                ?? Task.FromResult(new NavInfo { Mid = 123 });
        }

        public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
            => Task.FromResult(new LiveRoomInfo { Title = "test", RoomId = roomId, LiveStatus = 0 });

        public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
            => Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = "", Items = [] });

        public Task<System.Text.Json.JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
            => Task.FromResult(System.Text.Json.JsonDocument.Parse("""{"mid":0}""").RootElement.Clone());
    }

    private sealed class MockCookiePoolService : ICookiePoolService
    {
        public List<CookieRecord> ActiveCookies { get; set; } = [];
        public List<(string Cookie, TimeSpan Duration)> CooldownCalls { get; } = [];
        public List<string> OfflineCalls { get; } = [];

        public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CookieRecord>>(ActiveCookies);

        public Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default)
            => Task.FromResult(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });

        public Task<CookieRecord> NextAsync(CancellationToken ct = default)
            => Task.FromResult(new CookieRecord { Cookie = "next", Status = CookieStatus.Active });

        public Task MarkOfflineAsync(string cookie, CancellationToken ct = default)
        {
            OfflineCalls.Add(cookie);
            return Task.CompletedTask;
        }

        public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default)
        {
            CooldownCalls.Add((cookie, duration));
            return Task.CompletedTask;
        }
    }

    private static BilibiliCrawlerOptions DefaultOptions() => new()
    {
        CookieProbeIntervalMinutes = 5,
        CooldownMinutes = 30,
    };

    [Fact]
    public async Task RunOnceAsync_NormalException_MarksOffline()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => throw new InvalidOperationException("test error")
        };
        var cookiePool = new MockCookiePoolService
        {
            ActiveCookies = [new CookieRecord { Id = ObjectId.GenerateNewId(), Cookie = "cookie1", Status = CookieStatus.Active }]
        };
        var job = new CookieProbeJob(bili, cookiePool, DefaultOptions(), NullLogger<CookieProbeJob>.Instance);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Single(cookiePool.OfflineCalls);
        Assert.Equal("cookie1", cookiePool.OfflineCalls[0]);
        Assert.Empty(cookiePool.CooldownCalls);
    }

    [Fact]
    public async Task RunOnceAsync_RiskControlException_MarksCooldown()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => throw new RiskControlException("risk control", "http://test", "cookie1")
        };
        var cookiePool = new MockCookiePoolService
        {
            ActiveCookies = [new CookieRecord { Id = ObjectId.GenerateNewId(), Cookie = "cookie1", Status = CookieStatus.Active }]
        };
        var job = new CookieProbeJob(bili, cookiePool, DefaultOptions(), NullLogger<CookieProbeJob>.Instance);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Single(cookiePool.CooldownCalls);
        Assert.Equal("cookie1", cookiePool.CooldownCalls[0].Cookie);
        Assert.Equal(TimeSpan.FromMinutes(30), cookiePool.CooldownCalls[0].Duration);
        Assert.Empty(cookiePool.OfflineCalls);
    }

    [Fact]
    public async Task RunOnceAsync_OperationCanceledException_Throws()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, ct) => throw new OperationCanceledException(ct)
        };
        var cookiePool = new MockCookiePoolService
        {
            ActiveCookies = [new CookieRecord { Id = ObjectId.GenerateNewId(), Cookie = "cookie1", Status = CookieStatus.Active }]
        };
        var job = new CookieProbeJob(bili, cookiePool, DefaultOptions(), NullLogger<CookieProbeJob>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => job.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunOnceAsync_NavMidZero_MarksOffline()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => Task.FromResult(new NavInfo { Mid = 0 })
        };
        var cookiePool = new MockCookiePoolService
        {
            ActiveCookies = [new CookieRecord { Id = ObjectId.GenerateNewId(), Cookie = "cookie1", Status = CookieStatus.Active }]
        };
        var job = new CookieProbeJob(bili, cookiePool, DefaultOptions(), NullLogger<CookieProbeJob>.Instance);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Single(cookiePool.OfflineCalls);
        Assert.Equal("cookie1", cookiePool.OfflineCalls[0]);
    }

    [Fact]
    public async Task RunOnceAsync_CrawlBackoffActiveException_Skips()
    {
        var bili = new MockBiliApiClient
        {
            NavInfoFunc = (_, _) => throw new CrawlBackoffActiveException("backoff", TimeSpan.FromSeconds(10))
        };
        var cookiePool = new MockCookiePoolService
        {
            ActiveCookies = [new CookieRecord { Id = ObjectId.GenerateNewId(), Cookie = "cookie1", Status = CookieStatus.Active }]
        };
        var job = new CookieProbeJob(bili, cookiePool, DefaultOptions(), NullLogger<CookieProbeJob>.Instance);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Empty(cookiePool.CooldownCalls);
        Assert.Empty(cookiePool.OfflineCalls);
    }
}