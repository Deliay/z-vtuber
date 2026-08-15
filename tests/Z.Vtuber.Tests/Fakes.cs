using System.Text.Json;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawlers;
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
}

public sealed class FakeCookiePoolService : ICookiePoolService
{
    public string Cookie { get; set; } = "fake-cookie";

    public Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default) =>
        Task.FromResult(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });

    public Task<CookieRecord> NextAsync(CancellationToken ct = default) =>
        Task.FromResult(new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active });

    public Task MarkOfflineAsync(string cookie, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookieRecord>>([new CookieRecord { Cookie = Cookie, Status = CookieStatus.Active }]);
}
