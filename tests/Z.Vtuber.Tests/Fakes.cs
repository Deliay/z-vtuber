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

    public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default) =>
        Task.FromResult(LiveRoomInfoProvider?.Invoke(roomId)
            ?? new LiveRoomInfo { Title = "title", RoomId = roomId, LiveStatus = 1 });

    public Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default) =>
        Task.FromResult(new NavInfo { Mid = 1 });

    public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default) =>
        Task.FromResult(new SpaceDynamicsPage { HasMore = false, Offset = string.Empty, Items = [] });

    public Func<long, int, GuardInfo>? RoomGuardListProvider { get; set; }

    public Task<GuardInfo> GetRoomGuardListAsync(string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default) =>
        Task.FromResult(RoomGuardListProvider?.Invoke(roomId, page)
            ?? new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] });
}

public sealed class FakeCookiePoolService : ICookiePoolService
{
    public string Cookie { get; set; } = "fake-cookie";

    public Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default) =>
        Task.FromResult(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });

    public Task<CookieRecord> NextAsync(CancellationToken ct = default) =>
        Task.FromResult(new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active });

    public Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default) => Task.FromResult(true);

    public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookieRecord>>([new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active }]);

    public Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default) =>
        Task.FromResult<CookieRecord?>(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });
}

public sealed class FakeCookieNavProbeService : ICookieNavProbeService
{
    public Func<CookieRecord, CancellationToken, Task<CookieProbeOutcome>>? ProbeFunc { get; set; }
    public int CallCount { get; private set; }

    public Task<CookieProbeOutcome> ProbeAsync(CookieRecord record, CancellationToken ct = default)
    {
        CallCount++;
        return ProbeFunc?.Invoke(record, ct) ?? Task.FromResult(CookieProbeOutcome.Success);
    }
}

public sealed class ProbeServiceProvider : IServiceProvider
{
    public ICookieNavProbeService? Probe { get; set; }

    public object? GetService(Type serviceType) =>
        serviceType == typeof(ICookieNavProbeService) ? Probe : null;
}

public sealed class FakeBrowserSessionManager : IBrowserSessionManager
{
    public Func<Task<Z.Vtuber.Cookie.Browser.LoginSessionResult>>? CreateLoginSessionAsyncFunc { get; set; }
    public Func<string, TimeSpan, CancellationToken, Task<string?>>? WaitForNavResponseAsyncFunc { get; set; }
    public Func<string, Task<string>>? ExtractCookiesAsyncFunc { get; set; }
    public Func<string, Task>? DeleteSessionAsyncFunc { get; set; }
    public Func<string, Task>? RefreshSessionAsyncFunc { get; set; }
    public List<string> CreatedSessions { get; } = [];
    public List<string> DeletedSessions { get; } = [];
    public List<string> RefreshedSessions { get; } = [];

    public Task<Z.Vtuber.Cookie.Browser.LoginSessionResult> CreateLoginSessionAsync(CancellationToken ct = default)
    {
        var accountId = Guid.NewGuid().ToString("N");
        CreatedSessions.Add(accountId);
        return CreateLoginSessionAsyncFunc?.Invoke() ?? Task.FromResult(new Z.Vtuber.Cookie.Browser.LoginSessionResult
        {
            AccountId = accountId,
            QrCodeImage = "fake-qr-base64",
            ExpiresAt = DateTime.UtcNow.AddMinutes(3),
        });
    }

    public Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default)
    {
        return WaitForNavResponseAsyncFunc?.Invoke(accountId, timeout, ct) ?? Task.FromResult<string?>("{\"isLogin\":true}");
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
    public Func<string, TimeSpan, TimeSpan, CancellationToken, Task<IDistributedLockHandle?>>? AcquireAsyncFunc { get; set; }
    public List<string> AcquiredKeys { get; } = [];
    public List<string> ReleasedKeys { get; } = [];

    public Task<IDistributedLockHandle?> AcquireAsync(string key, TimeSpan ttl, TimeSpan waitTimeout, CancellationToken ct = default)
    {
        AcquiredKeys.Add(key);
        if (AcquireAsyncFunc is not null)
        {
            return AcquireAsyncFunc(key, ttl, waitTimeout, ct);
        }
        // 默认返回一个假的锁句柄
        return Task.FromResult<IDistributedLockHandle?>(new FakeDistributedLockHandle(key, this));
    }

    public void ReleaseKey(string key)
    {
        ReleasedKeys.Add(key);
    }
}

public sealed class FakeDistributedLockHandle : IDistributedLockHandle
{
    private readonly FakeDistributedLockProvider _provider;
    public string Key { get; }
    public bool IsDisposed { get; private set; }

    public FakeDistributedLockHandle(string key, FakeDistributedLockProvider provider)
    {
        Key = key;
        _provider = provider;
    }

    public ValueTask DisposeAsync()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            _provider.ReleaseKey(Key);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class MockCookieAccountInfoRepository : ICookieAccountInfoRepository
{
    public List<CookieAccountInfoRecord> Upserts { get; } = [];
    public bool ThrowOnUpsert { get; set; }

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
        => Task.FromResult(new Dictionary<MongoDB.Bson.ObjectId, CookieAccountInfoRecord>());
}

public sealed class MockCookiePoolService : ICookiePoolService
{
    public List<CookieRecord> ActiveCookies { get; set; } = [];
    public List<(string Cookie, TimeSpan Duration)> CooldownCalls { get; } = [];
    public List<string> OfflineCalls { get; } = [];
    public Func<string, CookieRecord?>? FindByCookieFunc { get; set; }
    public bool MarkOfflineReturns { get; set; } = true;

    public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CookieRecord>>(ActiveCookies);

    public Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default)
        => Task.FromResult(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });

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

public sealed class FakeCookieInvalidEventPublisher : ICookieInvalidEventPublisher
{
    public List<(string CookieId, string Reason)> PublishCalls { get; } = [];
    public bool ThrowOnPublish { get; set; }

    public Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("publish failed");
        }

        PublishCalls.Add((cookieId, reason));
        return Task.CompletedTask;
    }
}

public sealed class FakeCookieServiceInvalidEventPublisher : ICookieInvalidEventPublisher
{
    public List<(string CookieId, string Reason)> PublishCalls { get; } = [];

    public Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        PublishCalls.Add((cookieId, reason));
        return Task.CompletedTask;
    }
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
    public List<string?> CalledSinceIds { get; } = [];

    public Task<WeiboTimelinePage> GetUserTimelineAsync(long uid, string? sinceId = null, CancellationToken ct = default)
    {
        CalledSinceIds.Add(sinceId);
        var provider = sinceId is null ? OnRiskProvider : null;
        return Task.FromResult(provider?.Invoke(sinceId)
            ?? PageProvider?.Invoke(sinceId)
            ?? new WeiboTimelinePage());
    }
}
