using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.Http.Bilibili.Model;

namespace Z.Vtuber.Crawler.Crawlers;

public sealed class SpaceDynamicItem
{
    public string DynamicId { get; set; } = string.Empty;
    public JsonElement Data { get; set; }
}

public sealed class SpaceDynamicsPage
{
    public bool HasMore { get; set; }
    public string Offset { get; set; } = string.Empty;
    public IReadOnlyList<SpaceDynamicItem> Items { get; set; } = [];
}

public interface IBiliApiClient
{
    Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default);
    Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookieId, string cookie, long roomId, CancellationToken ct = default);
    Task<NavInfo> GetNavInfoV2Async(string cookieId, string cookie, CancellationToken ct = default);
    Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookieId, string cookie, long mid, string offset, CancellationToken ct = default);
    Task<GuardInfo> GetRoomGuardListAsync(string cookieId, string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default);
}

public sealed class BiliApiClient(IBilibiliAccountProvider accountProvider) : IBiliApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var raw = await http.GetAsync($"https://ukamnads.icu/api/v2/channel?uId={mid}", ct);
        raw.EnsureSuccessStatusCode();
        using var stream = await raw.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public async Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookieId, string cookie, long roomId, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookieId, cookie, ct);
        var crawler = provider.GetRequiredService<BiliLiveCrawler>();
        return await crawler.GetLiveRoomInfo(roomId, ct);
    }

    public async Task<NavInfo> GetNavInfoV2Async(string cookieId, string cookie, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookieId, cookie, ct);
        var basic = provider.GetRequiredService<BiliBasicInfoCrawler>();
        return await basic.GetNavInfoV2(ct);
    }

    public async Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookieId, string cookie, long mid, string offset, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookieId, cookie, ct);
        var account = provider.GetRequiredService<BilibiliAccount>();

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("host_mid", $"{mid}"),
            new("platform", "web"),
            new("features", "itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote,forwardListHidden,decorationCard,commentsNewVersion,onlyfansAssetsV2,ugcDelete,onlyfansQaCard,avatarAutoTheme,sunflowerStyle,cardsEnhance,eva3CardOpus,eva3CardVideo,eva3CardComment,eva3CardUser"),
            new("web_location", "0.0"),
        };
        if (!string.IsNullOrEmpty(offset))
        {
            parameters.Add(new("offset", offset));
        }

        var query = account.Sign(parameters);
        var http = provider.GetRequiredService<BiliLiveCrawler>();
        var raw = await http.Client.GetAsync($"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?{query}", ct);
        raw.EnsureSuccessStatusCode();
        using var stream = await raw.Content.ReadAsStreamAsync(ct);
        var response = await JsonSerializer.DeserializeAsync<BilibiliApiResponse<JsonElement>>(stream, JsonOptions, ct);
        var root = EnsureSuccess(response!).Data;

        var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) && hasMoreElement.ValueKind == JsonValueKind.True;
        var nextOffset = root.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.String
            ? offsetElement.GetString() ?? string.Empty
            : string.Empty;

        var items = new List<SpaceDynamicItem>();
        if (root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                var dynamicId = item.TryGetProperty("id_str", out var idElement)
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;
                items.Add(new SpaceDynamicItem { DynamicId = dynamicId, Data = item.Clone() });
            }
        }

        return new SpaceDynamicsPage { HasMore = hasMore, Offset = nextOffset, Items = items };
    }

    public async Task<GuardInfo> GetRoomGuardListAsync(string cookieId, string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookieId, cookie, ct);
        var crawler = provider.GetRequiredService<BiliLiveCrawler>();
        return await crawler.GetRoomGuardList(roomId, bId, page, ct);
    }

    private static BilibiliApiResponse<T> EnsureSuccess<T>(BilibiliApiResponse<T> response)
    {
        if (response.Code != 0)
        {
            throw new InvalidOperationException($"bilibili api returned code {response.Code}");
        }
        return response;
    }
}
