using System.Text.Json;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Crawler.Weibo;
using Z.Vtuber.Notifier.Milky;

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
