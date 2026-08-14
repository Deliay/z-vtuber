using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
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

public sealed class BiliApiClient(BilibiliCrawlerOptions options, IBilibiliAccountProvider accountProvider) : IBiliApiClient
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
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var account = provider.GetRequiredService<BilibiliAccount>();
        var query = account.Sign([new KeyValuePair<string, string>("mid", $"{mid}")]);
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var response = await crawler.GetAsync($"https://api.bilibili.com/x/space/acc/info?{query}", ct);
        return EnsureSuccess(response).Data;
    }

    public async Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var account = provider.GetRequiredService<BilibiliAccount>();
        var query = account.Sign([new KeyValuePair<string, string>("room_id", $"{roomId}")]);
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var response = await crawler.GetAsync($"https://api.live.bilibili.com/room/v1/Room/get_info?{query}", ct);
        return EnsureSuccess(response).Data.Deserialize<LiveRoomInfo>(JsonOptions);
    }

    public async Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var account = provider.GetRequiredService<BilibiliAccount>();
        return new NavInfo { Mid = account.Mid };
    }

    public async Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var account = provider.GetRequiredService<BilibiliAccount>();

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("host_mid", $"{mid}"),
        };
        if (!string.IsNullOrEmpty(offset))
        {
            parameters.Add(new("offset", offset));
        }

        var query = account.Sign(parameters);
        using var http = CreateHttpClient();
        var crawler = CreateCrawler(http, cookie);
        var response = await crawler.GetAsync($"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?{query}", ct);
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
