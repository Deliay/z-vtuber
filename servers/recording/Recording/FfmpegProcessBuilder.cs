using Z.Vtuber.Recording.Live;

namespace Z.Vtuber.Recording.Recording;

/// <summary>ffmpeg 启动命令（可测试的纯数据）。</summary>
public sealed record FfmpegCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// ffmpeg 单进程多输出参数构造：RAW copy + AV1(av1_nvenc) 二压 + 实时音频 pipe。
/// </summary>
public static class FfmpegProcessBuilder
{
    public const string AudioPipe = "pipe:1";
    public const double Av1BitrateRatio = 0.9;

    public static FfmpegCommand Build(
        LiveStreamCandidate stream,
        string rawFilePath,
        string? av1FilePath,
        string cookie,
        string referer,
        string userAgent,
        int? rawBitrateKbps,
        int defaultBitrateKbps,
        bool av1Enabled,
        string ffmpegPath = "ffmpeg")
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "warning",
        };

        if (RequiresHeaders(stream))
        {
            args.Add("-headers");
            args.Add($"Cookie: {cookie}\r\nReferer: {referer}\r\nUser-Agent: {userAgent}\r\n");
        }

        args.Add("-i");
        args.Add(stream.Url);

        // RAW：原样封装
        args.AddRange(["-map", "0", "-c", "copy", "-f", "flv", rawFilePath]);

        // AV1 二压：目标码率 = 原始码率 * 90%
        if (av1Enabled && !string.IsNullOrEmpty(av1FilePath))
        {
            var targetBitrateKbps = (int)Math.Round((rawBitrateKbps ?? defaultBitrateKbps) * Av1BitrateRatio);
            args.AddRange([
                "-map", "0:v", "-map", "0:a?",
                "-c:v", "av1_nvenc",
                "-rc", "vbr",
                "-spatial-aq", "1",
                "-temporal-aq", "1",
                "-multipass", "fullres",
                "-b:v", $"{targetBitrateKbps}k",
                "-c:a", "copy",
                "-f", "flv", av1FilePath,
            ]);
        }

        // 实时音频：stdout 供 Audio Hub 广播
        args.AddRange(["-map", "0:a", "-vn", "-c:a", "aac", "-b:a", "128k", "-f", "flv", AudioPipe]);

        return new FfmpegCommand(ffmpegPath, args);
    }

    /// <summary>http_stream 直连流需注入 headers；m3u8 由 ffmpeg 自行处理分片。</summary>
    public static bool RequiresHeaders(LiveStreamCandidate stream) =>
        string.Equals(stream.Protocol, "http_stream", StringComparison.OrdinalIgnoreCase)
        && string.Equals(stream.Format, "flv", StringComparison.OrdinalIgnoreCase);
}
