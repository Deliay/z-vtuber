using System.Text.Json;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawlers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

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

    public Task MarkOfflineAsync(string cookie, CancellationToken ct = default) => Task.CompletedTask;

    public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookieRecord>>([new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active }]);
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
