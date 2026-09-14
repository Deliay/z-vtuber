namespace Z.Vtuber.Recording.Live;

/// <summary>
/// playurl 候选流选取策略（纯函数）：排除 hevc，优先 http_stream+flv，fallback http_hls。
/// </summary>
public static class StreamResolver
{
    public static LiveStreamCandidate? Pick(IReadOnlyList<LiveStreamCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        // 排除 hevc（AV1 二压依赖完整解码，hevc 兼容性风险高）
        var filtered = candidates
            .Where(c => !string.Equals(c.Codec, "hevc", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            return null;
        }

        return filtered.FirstOrDefault(IsHttpStreamFlvAvc)
            ?? filtered.FirstOrDefault(IsHttpStreamFlv)
            ?? filtered.FirstOrDefault(IsHttpStream)
            ?? filtered.FirstOrDefault(IsHls)
            ?? filtered[0];
    }

    private static bool IsHttpStreamFlvAvc(LiveStreamCandidate c) =>
        IsHttpStreamFlv(c) && string.Equals(c.Codec, "avc", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpStreamFlv(LiveStreamCandidate c) =>
        string.Equals(c.Protocol, "http_stream", StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.Format, "flv", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpStream(LiveStreamCandidate c) =>
        string.Equals(c.Protocol, "http_stream", StringComparison.OrdinalIgnoreCase);

    private static bool IsHls(LiveStreamCandidate c) =>
        string.Equals(c.Protocol, "http_hls", StringComparison.OrdinalIgnoreCase)
        || string.Equals(c.Format, "m3u8", StringComparison.OrdinalIgnoreCase)
        || c.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
}
