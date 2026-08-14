using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.Http.Bilibili.Model;

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

public sealed class BiliApiClient(IBilibiliAccountProvider accountProvider) : IBiliApiClient
{
    public async Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var crawler = provider.GetRequiredService<BiliLiveCrawlerWrapper>();
        var account = provider.GetRequiredService<BilibiliAccount>();
        var query = account.Sign([new KeyValuePair<string, string>("mid", $"{mid}")]);
        var result = await crawler.GetAsync<BilibiliApiResponse<JsonElement>>(
            $"https://api.bilibili.com/x/space/acc/info?{query}", ct);
        return EnsureSuccess(result!).Data;
    }

    public async Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var crawler = provider.GetRequiredService<BiliLiveCrawler>();
        return await crawler.GetLiveRoomInfo(roomId, ct);
    }

    public async Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var crawler = provider.GetRequiredService<BiliLiveCrawlerWrapper>();
        var result = await crawler.GetAsync<BilibiliApiResponse<NavInfo>>(
            "https://api.bilibili.com/x/web-interface/nav", ct);
        return EnsureSuccess(result!).Data;
    }

    public async Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
    {
        var provider = await accountProvider.GetProviderAsync(cookie, ct);
        var crawler = provider.GetRequiredService<BiliLiveCrawlerWrapper>();
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
        var data = await crawler.GetAsync<BilibiliApiResponse<JsonElement>>(
            $"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?{query}", ct);
        var root = EnsureSuccess(data!).Data;

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

    private static BilibiliApiResponse<T> EnsureSuccess<T>(BilibiliApiResponse<T> response)
    {
        if (response.Code != 0)
        {
            throw new InvalidOperationException($"bilibili api returned code {response.Code}");
        }
        return response;
    }
}
