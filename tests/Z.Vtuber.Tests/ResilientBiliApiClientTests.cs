using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public sealed class ResilientBiliApiClientTests
{
    private sealed class TrackingBiliApiClient : IBiliApiClient
    {
        private int _callCount;
        public int CallCount => _callCount;
        public string? LastCookie { get; private set; }
        public Func<string, long, CancellationToken, Task<LiveRoomInfo>>? LiveRoomInfoFunc { get; set; }
        public Func<string, CancellationToken, Task<NavInfo>>? NavInfoFunc { get; set; }
        public Func<string, long, string, CancellationToken, Task<SpaceDynamicsPage>>? SpaceDynamicsFunc { get; set; }
        public Func<string, long, CancellationToken, Task<JsonElement>>? AccountInfoFunc { get; set; }
        public Func<string, long, long, int, CancellationToken, Task<GuardInfo>>? RoomGuardListFunc { get; set; }

        public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookieId, string cookie, long roomId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastCookie = cookie;
            return LiveRoomInfoFunc?.Invoke(cookie, roomId, ct)
                ?? Task.FromResult(new LiveRoomInfo { Title = "default", RoomId = roomId, LiveStatus = 0 });
        }

        public Task<NavInfo> GetNavInfoV2Async(string cookieId, string cookie, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastCookie = cookie;
            return NavInfoFunc?.Invoke(cookie, ct)
                ?? Task.FromResult(new NavInfo { Mid = 0 });
        }

        public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookieId, string cookie, long mid, string offset, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastCookie = cookie;
            return SpaceDynamicsFunc?.Invoke(cookie, mid, offset, ct)
                ?? Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = "", Items = [] });
        }

        public Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastCookie = cookie;
            return AccountInfoFunc?.Invoke(cookie, mid, ct)
                ?? Task.FromResult(JsonDocument.Parse("""{"mid":0}""").RootElement.Clone());
        }

        public Task<GuardInfo> GetRoomGuardListAsync(string cookieId, string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastCookie = cookie;
            return RoomGuardListFunc?.Invoke(cookie, roomId, bId, page, ct)
                ?? Task.FromResult(new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] });
        }
    }

    private static BilibiliCrawlerOptions DefaultOptions() => new()
    {
        FetchMode = BiliFetchMode.Auto,
        BackoffBaseSeconds = 30,
        BackoffMaxMinutes = 15,
        CooldownMinutes = 30,
        RiskControlCooldownThreshold = 5,
        ChannelRecoverSuccessCount = 3,
    };

    private static RiskControlException MakeRiskControl(string cookie) =>
        new("risk control", "http://test", cookie.Length >= 8 ? cookie[..8] : cookie);

    [Fact]
    public async Task DirectMode_UsesDirectClient()
    {
        var direct = new TrackingBiliApiClient();
        var browser = new TrackingBiliApiClient();
        var opts = DefaultOptions();
        opts.FetchMode = BiliFetchMode.Direct;
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await sut.GetLiveRoomInfoAsync("cookie1", 123);

        Assert.Equal(1, direct.CallCount);
        Assert.Equal(0, browser.CallCount);
    }

    [Fact]
    public async Task BrowserMode_UsesBrowserClient()
    {
        var direct = new TrackingBiliApiClient();
        var browser = new TrackingBiliApiClient();
        var opts = DefaultOptions();
        opts.FetchMode = BiliFetchMode.Browser;
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await sut.GetLiveRoomInfoAsync("cookie1", 123);

        Assert.Equal(0, direct.CallCount);
        Assert.Equal(1, browser.CallCount);
    }

    [Fact]
    public async Task AutoMode_DirectSuccess_ReportsSuccess()
    {
        var direct = new TrackingBiliApiClient();
        var browser = new TrackingBiliApiClient();
        var opts = DefaultOptions();
        var registry = new CrawlBackoffRegistry(Options.Create(opts));
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            registry, new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await sut.GetLiveRoomInfoAsync("cookie1", 123);

        var allowed = registry.TryAcquire("cookie1", out var wait);
        Assert.True(allowed);
        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public async Task AutoMode_DirectRiskControl_DegradesToBrowserAndRetries()
    {
        var direct = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, _, _) => throw MakeRiskControl("cookie12345678")
        };
        var browserCalled = false;
        var browser = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, roomId, _) =>
            {
                browserCalled = true;
                return Task.FromResult(new LiveRoomInfo { Title = "from-browser", RoomId = roomId, LiveStatus = 1 });
            }
        };
        var opts = DefaultOptions();
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        var result = await sut.GetLiveRoomInfoAsync("cookie12345678", 123);

        Assert.True(browserCalled);
        Assert.Equal("from-browser", result.Title);
        Assert.Equal(1, direct.CallCount);
        Assert.Equal(1, browser.CallCount);
    }

    [Fact]
    public async Task AutoMode_RiskControl_DegradesAndLogsCooldown()
    {
        var direct = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, _, _) => throw MakeRiskControl("cookie12345678")
        };
        var browserCalled = false;
        var browser = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, roomId, _) =>
            {
                browserCalled = true;
                return Task.FromResult(new LiveRoomInfo { Title = "from-browser", RoomId = roomId, LiveStatus = 1 });
            }
        };
        var opts = DefaultOptions();
        var cookieServiceClient = new FakeCookieServiceClient();
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), cookieServiceClient,
            NullLogger<ResilientBiliApiClient>.Instance);

        var result = await sut.GetLiveRoomInfoAsync("cookie12345678", 123);

        Assert.True(browserCalled);
        Assert.Equal("from-browser", result.Title);
        Assert.Single(cookieServiceClient.CooldownCalls);
        Assert.Equal(TimeSpan.FromMinutes(opts.CooldownMinutes), cookieServiceClient.CooldownCalls[0].Duration);
    }

    [Fact]
    public async Task TryAcquireBlocked_ThrowsCrawlBackoffActiveException()
    {
        var direct = new TrackingBiliApiClient();
        var browser = new TrackingBiliApiClient();
        var opts = DefaultOptions();
        var registry = new CrawlBackoffRegistry(Options.Create(opts));
        registry.ReportRiskControl("live:123");
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            registry, new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await Assert.ThrowsAsync<CrawlBackoffActiveException>(
            () => sut.GetLiveRoomInfoAsync("cookie1", 123));
    }

    [Fact]
    public async Task AutoMode_BrowserConsecutiveSuccess_RecoversToDirect()
    {
        var direct = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, _, _) => throw MakeRiskControl("cookie12345678")
        };
        var browserCallCount = 0;
        var browser = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, roomId, _) =>
            {
                Interlocked.Increment(ref browserCallCount);
                return Task.FromResult(new LiveRoomInfo { Title = "browser", RoomId = roomId, LiveStatus = 1 });
            }
        };
        var opts = DefaultOptions();
        opts.ChannelRecoverSuccessCount = 3;
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        // Trigger degradation
        await sut.GetLiveRoomInfoAsync("cookie12345678", 123);
        Assert.Equal(1, browserCallCount);

        // 2 more browser successes
        await sut.GetLiveRoomInfoAsync("cookie12345678", 123);
        await sut.GetLiveRoomInfoAsync("cookie12345678", 123);
        Assert.Equal(3, browserCallCount);

        // Now direct works again - switch direct func back
        direct.LiveRoomInfoFunc = (_, roomId, _) =>
            Task.FromResult(new LiveRoomInfo { Title = "direct-now", RoomId = roomId, LiveStatus = 1 });

        // Next call should use direct (recovered)
        var result = await sut.GetLiveRoomInfoAsync("cookie12345678", 123);
        Assert.Equal("direct-now", result.Title);
        Assert.Equal(3, browserCallCount); // no more browser calls
    }

    [Fact]
    public async Task DirectFetchMode_NeverSwitchesOnRiskControl()
    {
        var direct = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, _, _) => throw MakeRiskControl("cookie1")
        };
        var browser = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, roomId, _) =>
                Task.FromResult(new LiveRoomInfo { Title = "browser", RoomId = roomId, LiveStatus = 1 })
        };
        var opts = DefaultOptions();
        opts.FetchMode = BiliFetchMode.Direct;
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        var ex = await Assert.ThrowsAsync<RiskControlException>(
            () => sut.GetLiveRoomInfoAsync("cookie1", 123));

        Assert.NotNull(ex);
        Assert.Equal(1, direct.CallCount);
        Assert.Equal(0, browser.CallCount);
    }

    [Fact]
    public async Task BrowserFetchMode_NeverFallsBackToDirect()
    {
        var direct = new TrackingBiliApiClient();
        var browser = new TrackingBiliApiClient
        {
            LiveRoomInfoFunc = (_, roomId, _) =>
                Task.FromResult(new LiveRoomInfo { Title = "browser", RoomId = roomId, LiveStatus = 1 })
        };
        var opts = DefaultOptions();
        opts.FetchMode = BiliFetchMode.Browser;
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await sut.GetLiveRoomInfoAsync("cookie1", 123);

        Assert.Equal(0, direct.CallCount);
        Assert.Equal(1, browser.CallCount);
    }

    [Fact]
    public async Task GetRoomGuardListAsync_BackoffActiveOnGuardRoomIdKey_ThrowsBackoff()
    {
        var direct = new TrackingBiliApiClient();
        var browser = new TrackingBiliApiClient();
        var opts = DefaultOptions();
        var registry = new CrawlBackoffRegistry(Options.Create(opts));
        // 退避键必须为 guard:{roomId}，预置退避后调用应抛 CrawlBackoffActiveException
        registry.ReportRiskControl("guard:123");
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            registry, new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await Assert.ThrowsAsync<CrawlBackoffActiveException>(
            () => sut.GetRoomGuardListAsync("cookie1", 123, 456));

        Assert.Equal(0, direct.CallCount);
        Assert.Equal(0, browser.CallCount);
    }

    [Fact]
    public async Task GetRoomGuardListAsync_DirectRiskControl_DegradesToBrowser()
    {
        var direct = new TrackingBiliApiClient
        {
            RoomGuardListFunc = (_, _, _, _, _) => throw MakeRiskControl("cookie12345678")
        };
        var browserCalled = false;
        var browser = new TrackingBiliApiClient
        {
            RoomGuardListFunc = (_, roomId, bId, page, _) =>
            {
                browserCalled = true;
                return Task.FromResult(new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] });
            }
        };
        var opts = DefaultOptions();
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        await sut.GetRoomGuardListAsync("cookie12345678", 123, 456);

        Assert.True(browserCalled);
        Assert.Equal(1, direct.CallCount);
        Assert.Equal(1, browser.CallCount);
    }

    [Fact]
    public async Task GetAccountInfoDataAsync_AlwaysUsesDirect()
    {
        var direct = new TrackingBiliApiClient
        {
            AccountInfoFunc = (_, _, _) =>
                Task.FromResult(JsonDocument.Parse("""{"mid":123}""").RootElement.Clone())
        };
        var browser = new TrackingBiliApiClient
        {
            AccountInfoFunc = (_, _, _) =>
                Task.FromResult(JsonDocument.Parse("""{"mid":456}""").RootElement.Clone())
        };
        var opts = DefaultOptions();
        opts.FetchMode = BiliFetchMode.Browser;
        var sut = new ResilientBiliApiClient(
            direct, browser, opts,
            new CrawlBackoffRegistry(Options.Create(opts)), new FakeCookieServiceClient(),
            NullLogger<ResilientBiliApiClient>.Instance);

        var result = await sut.GetAccountInfoDataAsync("cookie1", 123);

        Assert.Equal(1, direct.CallCount);
        Assert.Equal(0, browser.CallCount);
        Assert.Equal(123, result.GetProperty("mid").GetInt64());
    }
}
