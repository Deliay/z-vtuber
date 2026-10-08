using System.Collections.Concurrent;
using System.Text.Json;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Crawler.Weibo;
using Z.Vtuber.Notifier.Milky;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Tests;

public sealed class FakeBiliApiClient : IBiliApiClient
{
    public Func<long, JsonElement>? AccountInfoProvider { get; set; }

    public Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
    {
        var data = AccountInfoProvider?.Invoke(mid)
            ?? JsonDocument.Parse("""{"mid":0}""").RootElement.Clone();
        return Task.FromResult(data);
    }

    public Func<long, LiveRoomInfo>? LiveRoomInfoProvider { get; set; }

    public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookieId, string cookie, long roomId, CancellationToken ct = default) =>
        Task.FromResult(LiveRoomInfoProvider?.Invoke(roomId)
            ?? new LiveRoomInfo { Title = "title", RoomId = roomId, LiveStatus = 1 });

    public Task<NavInfo> GetNavInfoV2Async(string cookieId, string cookie, CancellationToken ct = default) =>
        Task.FromResult(new NavInfo { Mid = 1 });

    public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookieId, string cookie, long mid, string offset, CancellationToken ct = default) =>
        Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = string.Empty, Items = [] });

    public Func<long, int, GuardInfo>? RoomGuardListProvider { get; set; }

    public Task<GuardInfo> GetRoomGuardListAsync(string cookieId, string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default) =>
        Task.FromResult(RoomGuardListProvider?.Invoke(roomId, page)
            ?? new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] });

    public Func<string, string, string, int, CancellationToken, Task<VideoPlayInfo>>? VideoPlayInfoFunc { get; set; }

    public Task<VideoPlayInfo> GetVideoPlayInfoAsync(string cookieId, string cookie, string bvid, int page, CancellationToken ct = default) =>
        VideoPlayInfoFunc?.Invoke(cookieId, cookie, bvid, page, ct)
        ?? Task.FromResult(new VideoPlayInfo { Title = "title", Cid = 1, PageCount = 1 });
}

public sealed class FakeCookiePoolService : ICookiePoolService
{
    public string Cookie { get; set; } = "fake-cookie";
    public Func<string, CancellationToken, Task<CookieRecord?>>? FindByCookieIdAsyncFunc { get; set; }

    public Task<CookieRecord> AddAsync(string cookie, string? accountId = null, CancellationToken ct = default) =>
        Task.FromResult(new CookieRecord { Id = MongoDB.Bson.ObjectId.GenerateNewId(), Cookie = cookie, AccountId = accountId, Status = CookieStatus.Active });

    public Task<CookieRecord> NextAsync(CancellationToken ct = default) =>
        Task.FromResult(new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active });

    public Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default) => Task.FromResult(true);

    public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookieRecord>>([new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active }]);

    public Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default) =>
        Task.FromResult<CookieRecord?>(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });

    public Task<CookieRecord?> FindByCookieIdAsync(string cookieId, CancellationToken ct = default) =>
        FindByCookieIdAsyncFunc?.Invoke(cookieId, ct)
        ?? Task.FromResult<CookieRecord?>(new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active });

    public Task UpdateAsync(string cookieId, MongoDB.Driver.UpdateDefinition<CookieRecord> update, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed class FakeBrowserSessionManager : IBrowserSessionManager
{
    public Func<string, Task<Z.Vtuber.Cookie.Browser.LoginSessionResult>>? CreateLoginSessionAsyncFunc { get; set; }
    public Func<string, TimeSpan, CancellationToken, Task<string?>>? WaitForNavResponseAsyncFunc { get; set; }
    public Func<string, Task<string>>? ExtractCookiesAsyncFunc { get; set; }
    public Func<string, Task>? DeleteSessionAsyncFunc { get; set; }
    public Func<string, Task>? RefreshSessionAsyncFunc { get; set; }
    public List<string> CreatedSessions { get; } = [];
    public List<string> DeletedSessions { get; } = [];
    public List<string> RefreshedSessions { get; } = [];

    public Task<Z.Vtuber.Cookie.Browser.LoginSessionResult> CreateLoginSessionAsync(string accountId, CancellationToken ct = default)
    {
        CreatedSessions.Add(accountId);
        return CreateLoginSessionAsyncFunc?.Invoke(accountId) ?? Task.FromResult(new Z.Vtuber.Cookie.Browser.LoginSessionResult
        {
            AccountId = accountId,
            QrCodeImage = "fake-qr-base64",
            ExpiresAt = DateTime.UtcNow.AddMinutes(3),
        });
    }

    public Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default)
    {
        return WaitForNavResponseAsyncFunc?.Invoke(accountId, timeout, ct)
            ?? Task.FromResult<string?>("{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":123,\"uname\":\"fake\"}}");
    }

    public Task<string> ExtractCookiesAsync(string accountId, CancellationToken ct = default)
    {
        return ExtractCookiesAsyncFunc?.Invoke(accountId) ?? Task.FromResult("SESSDATA=fake; bili_jct=fake");
    }

    public Task DeleteSessionAsync(string accountId, CancellationToken ct = default)
    {
        DeletedSessions.Add(accountId);
        return DeleteSessionAsyncFunc?.Invoke(accountId) ?? Task.CompletedTask;
    }

    public Task RefreshSessionAsync(string accountId, CancellationToken ct = default)
    {
        RefreshedSessions.Add(accountId);
        return RefreshSessionAsyncFunc?.Invoke(accountId) ?? Task.CompletedTask;
    }
}

public sealed class FakeDistributedLockProvider : IDistributedLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    public TimeSpan? LastWaitTimeout { get; private set; }
    public Action<string>? LockAcquired { get; set; }

    /// <summary>AcquireAsync 进入等待前回调一次，供并发用例以信号量替代固定 Task.Delay 定位「已开始等待」。</summary>
    public Action<string>? LockWaitStarted { get; set; }

    public async Task<IDistributedLockHandle?> AcquireAsync(string key, TimeSpan ttl, TimeSpan waitTimeout, CancellationToken ct = default)
    {
        LastWaitTimeout = waitTimeout;
        var sem = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        LockWaitStarted?.Invoke(key);
        var deadline = DateTime.UtcNow + waitTimeout;
        while (true)
        {
            if (await sem.WaitAsync(TimeSpan.FromMilliseconds(50), ct))
            {
                LockAcquired?.Invoke(key);
                return new FakeDistributedLockHandle(key, sem);
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
        }
    }
}

public sealed class FakeDistributedLockHandle : IDistributedLockHandle
{
    private readonly SemaphoreSlim _semaphore;
    public string Key { get; }
    public bool IsDisposed { get; private set; }

    public FakeDistributedLockHandle(string key, SemaphoreSlim semaphore)
    {
        Key = key;
        _semaphore = semaphore;
    }

    public ValueTask DisposeAsync()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            _semaphore.Release();
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class MockCookieAccountInfoRepository : ICookieAccountInfoRepository
{
    public List<CookieAccountInfoRecord> Upserts { get; } = [];
    public bool ThrowOnUpsert { get; set; }
    public Dictionary<MongoDB.Bson.ObjectId, CookieAccountInfoRecord> LookupResults { get; set; } = new();

    public Task UpsertAsync(CookieAccountInfoRecord record, CancellationToken ct = default)
    {
        if (ThrowOnUpsert)
        {
            throw new InvalidOperationException("db down");
        }

        Upserts.Add(record);
        return Task.CompletedTask;
    }

    public Task<Dictionary<MongoDB.Bson.ObjectId, CookieAccountInfoRecord>> GetByCookieIdsAsync(
        IReadOnlyCollection<MongoDB.Bson.ObjectId> cookieIds, CancellationToken ct = default)
        => Task.FromResult(LookupResults);
}

public sealed class MockCookiePoolService : ICookiePoolService
{
    public List<CookieRecord> ActiveCookies { get; set; } = [];
    public List<(string Cookie, TimeSpan Duration)> CooldownCalls { get; } = [];
    public List<string> OfflineCalls { get; } = [];
    public Func<string, CookieRecord?>? FindByCookieFunc { get; set; }
    public Func<string, CookieRecord?>? FindByCookieIdFunc { get; set; }
    public bool MarkOfflineReturns { get; set; } = true;

    public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CookieRecord>>(ActiveCookies);

    public Task<CookieRecord> AddAsync(string cookie, string? accountId = null, CancellationToken ct = default)
        => Task.FromResult(new CookieRecord { Cookie = cookie, AccountId = accountId, Status = CookieStatus.Active });

    public Task<CookieRecord> NextAsync(CancellationToken ct = default)
        => Task.FromResult(new CookieRecord { Cookie = "next", Status = CookieStatus.Active });

    public Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default)
    {
        OfflineCalls.Add(cookie);
        // Simulate atomic Active→Offline transition: caller can override via MarkOfflineReturns
        if (!MarkOfflineReturns)
        {
            return Task.FromResult(false);
        }
        // First call returns true, subsequent calls return false
        if (_offlineTransitions.Add(cookie))
        {
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private readonly HashSet<string> _offlineTransitions = [];

    public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default)
    {
        CooldownCalls.Add((cookie, duration));
        return Task.CompletedTask;
    }

    public Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default)
        => Task.FromResult(FindByCookieFunc?.Invoke(cookie));

    public Task<CookieRecord?> FindByCookieIdAsync(string cookieId, CancellationToken ct = default)
        => Task.FromResult(FindByCookieIdFunc?.Invoke(cookieId));

    public Task UpdateAsync(string cookieId, MongoDB.Driver.UpdateDefinition<CookieRecord> update, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class FakeMilkyApiClient : IMilkyApiClient
{
    public Func<Task<LoginInfo>>? LoginInfoFunc { get; set; }
    public Func<Task<IReadOnlyList<GroupInfo>>>? GroupListFunc { get; set; }
    public Func<long, string, Task>? SendMessageFunc { get; set; }
    public List<(long GroupId, string Text)> SendMessageCalls { get; } = [];

    public Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct = default)
        => LoginInfoFunc?.Invoke() ?? Task.FromResult(new LoginInfo { UserId = 123, Nickname = "test-bot" });

    public Task<IReadOnlyList<GroupInfo>> GetGroupListAsync(CancellationToken ct = default)
        => GroupListFunc?.Invoke() ?? Task.FromResult<IReadOnlyList<GroupInfo>>([]);

    public async Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default)
    {
        if (SendMessageFunc is not null)
        {
            await SendMessageFunc(groupId, text);
        }
        SendMessageCalls.Add((groupId, text));
    }
}

public sealed class FakeCookieServiceClient : ICookieServiceClient
{
    public Func<CancellationToken, Task<CookieCheckoutResult>>? CheckoutFunc { get; set; }
    public List<string> ReportInvalidCalls { get; } = [];
    public List<(string CookieId, TimeSpan Duration)> CooldownCalls { get; } = [];

    public Func<CancellationToken, Task<WeiboCookieCheckoutResult>>? CheckoutWeiboFunc { get; set; }
    public Func<string, CancellationToken, Task<ReportInvalidResult>>? ReportWeiboInvalidFunc { get; set; }
    public List<string> ReportWeiboInvalidCalls { get; } = [];
    public List<(string CookieId, TimeSpan Duration)> CooldownWeiboCalls { get; } = [];

    public Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default)
    {
        return CheckoutFunc?.Invoke(ct) ?? Task.FromResult(new CookieCheckoutResult
        {
            CookieId = "fake-id",
            Cookie = "fake-cookie",
        });
    }

    public Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        ReportInvalidCalls.Add(cookieId);
        return Task.FromResult(new ReportInvalidResult { Status = ReportInvalidStatus.Refreshed, CookieId = cookieId, Cookie = cookieId });
    }

    public Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        CooldownCalls.Add((cookieId, duration));
        return Task.CompletedTask;
    }

    public Task<WeiboCookieCheckoutResult> CheckoutWeiboAsync(CancellationToken ct = default)
    {
        // 默认无可用登录 cookie（Available=false），微博链路回退匿名。
        return CheckoutWeiboFunc?.Invoke(ct)
            ?? Task.FromResult(new WeiboCookieCheckoutResult { Available = false });
    }

    public Task<ReportInvalidResult> ReportWeiboInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        ReportWeiboInvalidCalls.Add(cookieId);
        return ReportWeiboInvalidFunc?.Invoke(cookieId, ct)
            ?? Task.FromResult(new ReportInvalidResult
            {
                Status = ReportInvalidStatus.Refreshed,
                CookieId = cookieId,
                Cookie = $"refreshed-{cookieId}",
            });
    }

    public Task CooldownWeiboAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        CooldownWeiboCalls.Add((cookieId, duration));
        return Task.CompletedTask;
    }
}

public sealed class FakeWeiboLoginCookieProvider : IWeiboLoginCookieProvider
{
    public WeiboLoginCookie? Cookie { get; set; }
    public int GetCalls { get; private set; }
    public Func<string, CancellationToken, Task<ReportInvalidResult>>? ReportInvalidFunc { get; set; }
    public List<string> ReportInvalidCalls { get; } = [];

    public Task<WeiboLoginCookie?> GetAsync(CancellationToken ct = default)
    {
        GetCalls++;
        return Task.FromResult(Cookie);
    }

    public async Task<WeiboLoginCookieLease?> AcquireAsync(CancellationToken ct = default)
    {
        var cookie = await GetAsync(ct);
        if (cookie is null)
        {
            return null;
        }

        // 测试用：不经过真实分布式锁，包装为无操作句柄。
        return new WeiboLoginCookieLease(cookie, new NoopLockHandle($"cookie-call:{cookie.CookieId}"));
    }

    public Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        ReportInvalidCalls.Add(cookieId);
        return ReportInvalidFunc?.Invoke(cookieId, ct)
            ?? Task.FromResult(new ReportInvalidResult
            {
                Status = ReportInvalidStatus.Refreshed,
                CookieId = cookieId,
                Cookie = $"refreshed-{cookieId}",
            });
    }
}

public sealed class FakeWeiboCookieSource : IWeiboCookieSource
{
    public string Cookie { get; set; } = "SUB=a; SUBP=b";
    public bool IsLoginCookie { get; set; }
    public string? CookieId { get; set; }

    public Task<WeiboCookieLease> AcquireAsync(CancellationToken ct = default) =>
        Task.FromResult(new WeiboCookieLease(Cookie, IsLoginCookie, CookieId));
}

internal sealed class NoopLockHandle(string key) : IDistributedLockHandle
{
    public string Key { get; } = key;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeWeiboVisitorCookieProvider : IWeiboVisitorCookieProvider
{
    public string CookieHeader { get; set; } = "SUB=fake; SUBP=fake";
    public int RenewCount { get; private set; }
    public Func<CancellationToken, string>? CookieFunc { get; set; }

    public Task<string> GetOrRefreshAsync(CancellationToken ct = default) =>
        Task.FromResult(CookieFunc?.Invoke(ct) ?? CookieHeader);

    public Task RenewAsync(CancellationToken ct = default)
    {
        RenewCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeWeiboApiClient : IWeiboApiClient
{
    public Func<string?, WeiboTimelinePage>? PageProvider { get; set; }
    public Func<string?, WeiboTimelinePage>? OnRiskProvider { get; set; }

    /// <summary>按调用序号（0-based）响应，用于模拟「首次风控、重试成功」等序列。</summary>
    public Func<int, string?, WeiboTimelinePage>? SequencedProvider { get; set; }

    public List<string?> CalledSinceIds { get; } = [];

    private int _callCount;

    public Task<WeiboTimelinePage> GetUserTimelineAsync(long uid, string? sinceId = null, CancellationToken ct = default)
    {
        CalledSinceIds.Add(sinceId);
        var index = _callCount++;

        if (SequencedProvider is not null)
        {
            return Task.FromResult(SequencedProvider(index, sinceId));
        }

        var provider = sinceId is null ? OnRiskProvider : null;
        return Task.FromResult(provider?.Invoke(sinceId)
            ?? PageProvider?.Invoke(sinceId)
            ?? new WeiboTimelinePage());
    }
}
