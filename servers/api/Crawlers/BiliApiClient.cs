using System.Text.Json;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Configuration;

namespace Z.Vtuber.Crawlers;

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
    Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default);
    Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default);
    Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default);
}

public sealed class BiliApiClient(BilibiliCrawlerOptions options) : IBiliApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient CreateHttpClient() =>
        new() { Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds) };

    private static BiliRawCrawler CreateCrawler(HttpClient http, string cookie) => new(http, cookie);

    public async Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
    {
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var response = await crawler.GetAsync($"https://api.bilibili.com/x/space/acc/info?mid={mid}", ct);
        return EnsureSuccess(response).Data;
    }

    public async Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
    {
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var response = await crawler.GetAsync($"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={roomId}", ct);
        return EnsureSuccess(response).Data.Deserialize<LiveRoomInfo>(JsonOptions);
    }

    public async Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
    {
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var response = await crawler.GetAsync("https://api.bilibili.com/x/web-interface/nav", ct);
        return EnsureSuccess(response).Data.Deserialize<NavInfo>(JsonOptions);
    }

    public async Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
    {
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var url = $"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?host_mid={mid}";
        if (!string.IsNullOrEmpty(offset))
        {
            url += $"&offset={Uri.EscapeDataString(offset)}";
        }

        var response = await crawler.GetAsync(url, ct);
        var data = EnsureSuccess(response).Data;

        var hasMore = data.TryGetProperty("has_more", out var hasMoreElement) && hasMoreElement.ValueKind == JsonValueKind.True;
        var nextOffset = data.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.String
            ? offsetElement.GetString() ?? string.Empty
            : string.Empty;

        var items = new List<SpaceDynamicItem>();
        if (data.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
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

    private static BilibiliApiResponse<JsonElement> EnsureSuccess(BilibiliApiResponse<JsonElement> response)
    {
        if (response.Code != 0)
        {
            throw new InvalidOperationException($"bilibili api returned code {response.Code}");
        }

        return response;
    }
}

public sealed class BiliRawCrawler(HttpClient client, string cookie) : HttpCrawler(client, new CookieJar(cookie))
{
    public ValueTask<BilibiliApiResponse<JsonElement>> GetAsync(string url, CancellationToken ct) =>
        GetAsync<BilibiliApiResponse<JsonElement>>(url, ct);
}
