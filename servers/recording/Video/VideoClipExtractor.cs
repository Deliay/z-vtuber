using System.Globalization;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Recording.Video;

/// <summary>录播视频切片抽取：按 [startMs, endMs] 从录播文件截取原始编码片段并流式写出，ct 取消即 kill 进程。</summary>
public interface IVideoClipExtractor
{
    /// <summary>
    /// 从录播文件截取 [startMs, endMs] 片段并以 fragmented MP4 写入输出流；
    /// 切片失败（未写出任何字节）抛 <see cref="VideoClipFailedException"/>。
    /// </summary>
    Task ExtractAsync(string fullPath, long startMs, long endMs, Stream output, CancellationToken ct);
}

/// <summary>视频切片失败（ffmpeg 非零退出且未写出任何字节）。</summary>
public sealed class VideoClipFailedException() : Exception("video clip failed")
{
}

/// <summary>
/// 基于 ffmpeg 外部进程的录播视频切片：<c>-ss</c>/<c>-t</c> 前置为输入侧 seek（长录播秒开，
/// 起点由 ffmpeg 吸附到不晚于该时间的最近关键帧，与预览定位落点一致），<c>-c copy</c> 保留原始编码，
/// <c>-movflags frag_keyframe+empty_moov</c> 输出可流式传输的 fragmented MP4，<c>pipe:1</c> 直写输出流不落盘。
/// 进程封装沿用 <see cref="IRecordingProcessFactory"/> 口径（stderr 泵、取消/释放时 kill 并回收）。
/// </summary>
public sealed class VideoClipExtractor(
    RecordingOptions options,
    IRecordingProcessFactory processFactory,
    ILogger<VideoClipExtractor> logger) : IVideoClipExtractor
{
    /// <summary>ffmpeg 参数构造（纯函数口径，供单测断言顺序与切片形态）。</summary>
    public IReadOnlyList<string> BuildArguments(string fullPath, long startMs, long endMs)
    {
        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            // -ss/-t 置于 -i 之前：输入 seek，小时级文件秒开；-ss 会吸附到最近关键帧
            "-ss",
            FormatSeconds(startMs),
            "-t",
            FormatSeconds(endMs - startMs),
            "-i",
            fullPath,
            // 原始编码复制，零转码；音频可选（部分 RAW 文件可能无音轨）
            "-map",
            "0:v:0",
            "-map",
            "0:a:0?",
            "-c",
            "copy",
            // fragmented MP4：边生成边传输，无需回写 moov
            "-movflags",
            "frag_keyframe+empty_moov",
            // 输入 seek 可能产生负时间戳：独立片段时间轴从 0 开始
            "-avoid_negative_ts",
            "make_zero",
            "-f",
            "mp4",
            "pipe:1",
        ];
    }

    public async Task ExtractAsync(string fullPath, long startMs, long endMs, Stream output, CancellationToken ct)
    {
        var command = new FfmpegCommand(options.FfmpegPath, BuildArguments(fullPath, startMs, endMs));
        await using var process = processFactory.Start(command);

        long written = 0;
        int exitCode;
        try
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
            }

            exitCode = await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // ct / ResponseAborted 取消：kill 进程并回收（await using 释放时按 RecordingProcess 口径 kill + reap）
            process.Kill();
            throw;
        }

        if (exitCode != 0 && written == 0)
        {
            logger.LogWarning(
                "Video clip extraction failed: file={FullPath} startMs={StartMs} endMs={EndMs} exitCode={ExitCode}",
                fullPath, startMs, endMs, exitCode);
            throw new VideoClipFailedException();
        }

        // exitCode != 0 但已写出字节：响应头已按 200 发出，就地中止交由客户端报播放失败

        await output.FlushAsync(ct);
    }

    private static string FormatSeconds(long ms) =>
        (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}
