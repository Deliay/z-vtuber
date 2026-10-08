using System.Text.Json;
using Mikibot.Crawler.Http.Bilibili;

namespace Z.Vtuber.Crawler.Crawlers;

/// <summary>
/// B 站视频取流响应解析（纯函数，客户端无关）：<c>x/web-interface/view</c> 解析分 P 与标题，
/// <c>x/player/wbi/playurl</c> 解析 DASH / flv 候选并做 DASH 优先、flv 回退。
/// </summary>
public static class BiliVideoResponseParser
{
    private const string ViewUrl = "https://api.bilibili.com/x/web-interface/view";
    private const string PlayUrlUrl = "https://api.bilibili.com/x/player/wbi/playurl";

    /// <summary>
    /// 拉取并解析视频取流信息。<paramref name="getJson"/> 负责带签名/Cookie 访问并返回已克隆的 JSON 根节点。
    /// </summary>
    public static async Task<VideoPlayInfo> FetchAsync(
        BilibiliAccount account,
        Func<string, Task<JsonElement>> getJson,
        string bvid,
        int page,
        CancellationToken ct = default)
    {
        var viewRoot = await getJson($"{ViewUrl}?bvid={Uri.EscapeDataString(bvid)}");
        var view = ParseView(viewRoot, page);

        var playUrl = await FetchPlayUrlAsync(account, getJson, bvid, view.Cid);
        return new VideoPlayInfo
        {
            Title = view.Title,
            Cid = view.Cid,
            PageCount = view.PageCount,
            VideoStreams = playUrl.Video,
            AudioStreams = playUrl.Audio,
            IsFlvFallback = playUrl.FlvFallback,
        };
    }

    /// <summary>解析 view 响应，返回标题、目标分 P 的 cid 与总分 P 数；不可访问时抛 <see cref="VideoNotFoundException"/>，分 P 越界时抛 <see cref="VideoPageOutOfRangeException"/>。</summary>
    public static (string Title, long Cid, int PageCount) ParseView(JsonElement root, int page)
    {
        if (GetCode(root) != 0)
        {
            throw new VideoNotFoundException();
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new VideoNotFoundException();
        }

        var title = data.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString() ?? string.Empty
            : string.Empty;

        if (!data.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0)
        {
            throw new VideoNotFoundException();
        }

        var pageCount = pages.GetArrayLength();
        if (page > pageCount)
        {
            throw new VideoPageOutOfRangeException(pageCount);
        }

        var pageElement = pages[page - 1];
        var cid = pageElement.TryGetProperty("cid", out var cidElement) && cidElement.ValueKind == JsonValueKind.Number
            ? cidElement.GetInt64()
            : 0;
        if (cid <= 0)
        {
            throw new VideoNotFoundException();
        }

        return (title, cid, pageCount);
    }

    /// <summary>
    /// 解析 playurl 响应：DASH 优先（video/audio 各自候选），DASH 不可用时回退 durl 整段 flv。
    /// 返回 false 表示本次响应没有可用流（由调用方决定是否以 fnval=0 重试）；code 非 0 时抛 <see cref="VideoPlayUrlException"/>。
    /// </summary>
    public static bool TryParsePlayUrl(JsonElement root, out ParsedPlayUrl? parsed)
    {
        parsed = null;

        var code = GetCode(root);
        if (code != 0)
        {
            throw new VideoPlayUrlException(code);
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (data.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
        {
            var video = ParseDashCandidates(dash, "video");
            var audio = ParseDashCandidates(dash, "audio");
            if (video.Count > 0)
            {
                parsed = new ParsedPlayUrl(video, audio, FlvFallback: false);
                return true;
            }
        }

        if (data.TryGetProperty("durl", out var durl) && durl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in durl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && TryGetUrl(item, out var url) && !string.IsNullOrEmpty(url))
                {
                    parsed = new ParsedPlayUrl([new VideoStreamCandidate { Url = url }], [], FlvFallback: true);
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<ParsedPlayUrl> FetchPlayUrlAsync(
        BilibiliAccount account,
        Func<string, Task<JsonElement>> getJson,
        string bvid,
        long cid)
    {
        var dashRoot = await getJson(BuildPlayUrlUrl(account, bvid, cid, fnval: 16));
        if (TryParsePlayUrl(dashRoot, out var parsed))
        {
            return parsed!;
        }

        // DASH 不可用：回退 fnval=0 取整段 flv
        var fallbackRoot = await getJson(BuildPlayUrlUrl(account, bvid, cid, fnval: 0));
        if (TryParsePlayUrl(fallbackRoot, out parsed))
        {
            return parsed!;
        }

        throw new VideoPlayUrlException(-1);
    }

    private static string BuildPlayUrlUrl(BilibiliAccount account, string bvid, long cid, int fnval)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("bvid", bvid),
            new("cid", cid.ToString()),
            new("qn", "127"),
            new("fnval", fnval.ToString()),
            new("fourk", "1"),
        };
        return $"{PlayUrlUrl}?{account.Sign(parameters)}";
    }

    private static List<VideoStreamCandidate> ParseDashCandidates(JsonElement dash, string name)
    {
        var candidates = new List<VideoStreamCandidate>();
        if (!dash.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetUrl(item, out var url) || string.IsNullOrEmpty(url))
            {
                continue;
            }

            var qualityId = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt32()
                : 0;
            var bandwidth = item.TryGetProperty("bandwidth", out var bandwidthElement) && bandwidthElement.ValueKind == JsonValueKind.Number
                ? bandwidthElement.GetInt64()
                : 0;

            candidates.Add(new VideoStreamCandidate { Url = url, QualityId = qualityId, Bandwidth = bandwidth });
        }

        return candidates;
    }

    private static bool TryGetUrl(JsonElement item, out string url)
    {
        url = string.Empty;
        foreach (var name in (string[])["baseUrl", "base_url", "url"])
        {
            if (item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    url = value;
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetCode(JsonElement root) =>
        root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : -1;
}

/// <summary>playurl 解析结果。</summary>
public sealed record ParsedPlayUrl(
    IReadOnlyList<VideoStreamCandidate> Video,
    IReadOnlyList<VideoStreamCandidate> Audio,
    bool FlvFallback);
