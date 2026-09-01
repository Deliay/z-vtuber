using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Configuration;

namespace Z.Vtuber.Crawler.Crawlers;

public sealed class BrowserishBiliApiClient : IBiliApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly BilibiliCrawlerOptions _options;
    private readonly IBilibiliAccountProvider _accountProvider;
    private readonly Random _random = Random.Shared;
    private bool _warmedUp;
    private readonly SemaphoreSlim _warmupLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BrowserishBiliApiClient(HttpMessageHandler handler, BilibiliCrawlerOptions options, IBilibiliAccountProvider accountProvider)
    {
        _options = options;
        _accountProvider = accountProvider;
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
        };
        ConfigureDefaultHeaders();
    }

    public HttpClient HttpClient => _httpClient;

    private void ConfigureDefaultHeaders()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_options.BrowserUserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
    }

    private async Task EnsureWarmupAsync(CancellationToken ct = default)
    {
        if (_warmedUp) return;

        await _warmupLock.WaitAsync(ct);
        try
        {
            if (_warmedUp) return;

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
            request.Headers.Add("Cookie", "buvid3=test");

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            _warmedUp = true;
        }
        finally
        {
            _warmupLock.Release();
        }
    }

    private async Task DelayAsync(CancellationToken ct = default)
    {
        var delay = _random.Next(_options.BrowserMinDelaySeconds, _options.BrowserMaxDelaySeconds + 1);
        await Task.Delay(TimeSpan.FromSeconds(delay), ct);
    }

    public async Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
    {
        await EnsureWarmupAsync(ct);
        await DelayAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id={roomId}");
        request.Headers.Add("Cookie", cookie);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var apiResponse = await JsonSerializer.DeserializeAsync<BilibiliApiResponse<JsonElement>>(stream, JsonOptions, ct);

        if (apiResponse.Code != 0)
            throw new InvalidOperationException($"bilibili api returned code {apiResponse.Code}");

        var data = apiResponse.Data;
        var roomInfo = data.GetProperty("room_info");

        return new LiveRoomInfo
        {
            RoomId = roomInfo.GetProperty("room_id").GetInt64(),
            Title = roomInfo.GetProperty("title").GetString() ?? string.Empty,
            LiveStatus = roomInfo.GetProperty("live_status").GetInt32()
        };
    }

    public async Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
    {
        await EnsureWarmupAsync(ct);
        await DelayAsync(ct);

        var account = await _accountProvider.GetProviderAsync(cookie, ct);
        var biliAccount = account.GetRequiredService<BilibiliAccount>();
        
        // Use wbi signature for nav request
        var signedUrl = biliAccount.Sign([]);
        var url = $"https://api.bilibili.com/x/web-interface/nav?{signedUrl}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var apiResponse = await JsonSerializer.DeserializeAsync<BilibiliApiResponse<NavInfo>>(stream, JsonOptions, ct);

        if (apiResponse.Code != 0)
            throw new InvalidOperationException($"bilibili api returned code {apiResponse.Code}");

        return apiResponse.Data;
    }

    public async Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
    {
        await EnsureWarmupAsync(ct);
        await DelayAsync(ct);

        var account = await _accountProvider.GetProviderAsync(cookie, ct);
        var biliAccount = account.GetRequiredService<BilibiliAccount>();
        
        // Build parameters for wbi signature
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("host_mid", mid.ToString()),
            new("platform", "web")
        };
        if (!string.IsNullOrEmpty(offset))
            parameters.Add(new("offset", offset));
        
        var signedUrl = biliAccount.Sign(parameters);
        var url = $"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?{signedUrl}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var apiResponse = await JsonSerializer.DeserializeAsync<BilibiliApiResponse<JsonElement>>(stream, JsonOptions, ct);

        if (apiResponse.Code != 0)
            throw new InvalidOperationException($"bilibili api returned code {apiResponse.Code}");

        var data = apiResponse.Data;
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

    public async Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://ukamnads.icu/api/v2/channel?uId={mid}");
        using var raw = await _httpClient.SendAsync(request, ct);
        raw.EnsureSuccessStatusCode();
        using var stream = await raw.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _warmupLock.Dispose();
    }
}
