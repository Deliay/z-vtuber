namespace Z.Vtuber.Crawler.Crawlers;

/// <summary>一条候选取流（DASH 的 video/audio 分片，或 flv 回退的整段流）。</summary>
public sealed class VideoStreamCandidate
{
    public string Url { get; init; } = string.Empty;

    /// <summary>清晰度/音质档位 id（仅 DASH 有意义，flv 回退恒为 0）。</summary>
    public int QualityId { get; init; }

    /// <summary>码率（kbps，仅 DASH 有意义）。</summary>
    public long Bandwidth { get; init; }
}

/// <summary>
/// 视频取流解析结果：标题、分 P 信息与全部可用候选流（不在此处做模式过滤）。
/// </summary>
public sealed class VideoPlayInfo
{
    public string Title { get; init; } = string.Empty;
    public long Cid { get; init; }
    public int PageCount { get; init; }
    public IReadOnlyList<VideoStreamCandidate> VideoStreams { get; init; } = [];

    /// <summary>DASH 音频候选；flv 回退时为空。</summary>
    public IReadOnlyList<VideoStreamCandidate> AudioStreams { get; init; } = [];

    /// <summary>是否走了 flv 回退（无 DASH，仅整段流，无独立音频）。</summary>
    public bool IsFlvFallback { get; init; }
}

/// <summary>视频不存在或不可访问（已删除/私密/限制/审核中）。</summary>
public sealed class VideoNotFoundException : Exception
{
    public VideoNotFoundException()
        : base("Video not found or not accessible.")
    {
    }
}

/// <summary>请求的分 P 超出视频实际分 P 数。</summary>
public sealed class VideoPageOutOfRangeException : Exception
{
    public int PageCount { get; }

    public VideoPageOutOfRangeException(int pageCount)
        : base($"Requested page is out of range; video has {pageCount} page(s).")
    {
        PageCount = pageCount;
    }
}

/// <summary>取流地址接口返回非 0 code（含权限受限 -403）。</summary>
public sealed class VideoPlayUrlException : Exception
{
    public int Code { get; }

    public VideoPlayUrlException(int code)
        : base($"bilibili playurl returned code {code}.")
    {
        Code = code;
    }
}
