namespace Z.Vtuber.Crawler.Video;

/// <summary>下载内容类型：仅视频流 / 仅音频流。</summary>
public enum VideoStreamKind
{
    Video,
    Audio,
}

/// <summary>按下载模式筛选后确定的目标流。</summary>
public sealed record VideoStreamSelection(VideoStreamKind Kind, string Url, string ContentType, string Extension);

/// <summary>带 HTTP 状态码的下载领域异常；消息即对外可读中文原因。</summary>
public sealed class VideoDownloadException : Exception
{
    public int StatusCode { get; }

    public VideoDownloadException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// 一次下载的就绪结果：上游状态码与透传响应头元数据，以及可供逐段拷贝的上游流（不落盘）。
/// </summary>
public sealed class VideoDownloadResult : IDisposable
{
    public int StatusCode { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string? ContentDisposition { get; init; }
    public string? AcceptRanges { get; init; }
    public string? ContentRange { get; init; }
    public long? ContentLength { get; init; }
    public Stream? Content { get; init; }
    internal HttpResponseMessage? Upstream { get; init; }

    public void Dispose()
    {
        Content?.Dispose();
        Upstream?.Dispose();
    }
}
