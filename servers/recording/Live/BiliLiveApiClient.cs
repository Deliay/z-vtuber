using System.Text.Json;

namespace Z.Vtuber.Recording.Live;

public interface IBiliLiveApi
{
    Task<LiveRoomInfo> GetRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default);
    Task<IReadOnlyList<LiveStreamCandidate>> GetStreamCandidatesAsync(string cookie, long roomId, CancellationToken ct = default);
}

/// <summary>
/// 直连 B 站直播 API 的最小客户端：room get_info 与 playurl v2。
/// </summary>
public sealed class BiliLiveApiClient(HttpClient http, ILogger<BiliLiveApiClient> logger) : IBiliLiveApi
{
    private const string RoomInfoUrl = "https://api.live.bilibili.com/room/v1/Room/get_info";
    private const string PlayUrlUrl = "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo";

    public async Task<LiveRoomInfo> GetRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RoomInfoUrl}?room_id={roomId}");
        ApplyCookie(request, cookie);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        if (TryGetCode(root, out var code) && code != 0)
        {
            throw new InvalidOperationException($"bilibili room get_info returned code {code}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new LiveRoomInfo(roomId, string.Empty, false);
        }

        var liveStatus = data.TryGetProperty("live_status", out var ls) && ls.ValueKind == JsonValueKind.Number
            ? ls.GetInt32()
            : 0;
        var title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;

        return new LiveRoomInfo(roomId, title, liveStatus == 1);
    }

    public async Task<IReadOnlyList<LiveStreamCandidate>> GetStreamCandidatesAsync(
        string cookie, long roomId, CancellationToken ct = default)
    {
        var url = $"{PlayUrlUrl}?room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web&ptype=8&dolby=5&panorama=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCookie(request, cookie);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);

        var candidates = new List<LiveStreamCandidate>();
        if (TryGetCode(doc.RootElement, out var playUrlCode) && playUrlCode != 0)
        {
            throw new InvalidOperationException($"bilibili playurl returned code {playUrlCode}");
        }

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("playurl_info", out var playurlInfo)
            || playurlInfo.ValueKind != JsonValueKind.Object
            || !playurlInfo.TryGetProperty("playurl", out var playurl)
            || playurl.ValueKind != JsonValueKind.Object
            || !playurl.TryGetProperty("stream", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            logger.LogDebug("No playurl streams returned for room {RoomId}", roomId);
            return candidates;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            var protocol = stream.TryGetProperty("protocol_name", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            if (!stream.TryGetProperty("format", out var formats) || formats.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var format in formats.EnumerateArray())
            {
                var formatName = format.TryGetProperty("format_name", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                if (!format.TryGetProperty("codec", out var codecs) || codecs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var codec in codecs.EnumerateArray())
                {
                    var codecName = codec.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? string.Empty : string.Empty;
                    var baseUrl = codec.TryGetProperty("base_url", out var bu) ? bu.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        continue;
                    }

                    var host = string.Empty;
                    var extra = string.Empty;
                    if (codec.TryGetProperty("url_info", out var urlInfos) && urlInfos.ValueKind == JsonValueKind.Array)
                    {
                        var first = urlInfos.EnumerateArray().FirstOrDefault();
                        if (first.ValueKind == JsonValueKind.Object)
                        {
                            host = first.TryGetProperty("host", out var h) ? h.GetString() ?? string.Empty : string.Empty;
                            extra = first.TryGetProperty("extra", out var e) ? e.GetString() ?? string.Empty : string.Empty;
                        }
                    }

                    candidates.Add(new LiveStreamCandidate($"{host}{baseUrl}{extra}", protocol, formatName, codecName));
                }
            }
        }

        return candidates;
    }

    private static void ApplyCookie(HttpRequestMessage request, string cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
    }

    private static bool TryGetCode(JsonElement root, out int code)
    {
        code = -1;
        if (root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number)
        {
            code = codeElement.GetInt32();
            return true;
        }

        return false;
    }
}
