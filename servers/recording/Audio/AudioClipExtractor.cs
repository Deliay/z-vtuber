using System.Diagnostics;
using System.Globalization;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Recording.Audio;

/// <summary>录播音频片段抽取：按 [startMs, endMs] 从录播文件抽音频并流式写出，ct 取消即 kill 进程。</summary>
public interface IAudioClipExtractor
{
    /// <summary>片段输出 Content-Type：audio/mpeg（libmp3lame 可用）或 audio/wav（降级，语义不变）。</summary>
    string ContentType { get; }

    /// <summary>从录播文件抽取 [startMs, endMs] 音频并转码写入输出流；抽取失败（未写出任何字节）抛 <see cref="AudioClipFailedException"/>。</summary>
    Task ExtractAsync(string fullPath, long startMs, long endMs, Stream output, CancellationToken ct);
}

/// <summary>音频片段抽取失败（ffmpeg 非零退出且未写出任何字节）。</summary>
public sealed class AudioClipFailedException() : Exception("audio clip failed")
{
}

/// <summary>
/// mp3 编码能力：启动时探测一次（<c>ffmpeg -encoders</c> 是否含 libmp3lame），
/// 缺失时片段端点降级输出 wav（audio/wav，接口语义与参数不变）。
/// </summary>
public sealed class Mp3EncoderSupport
{
    public bool Available { get; set; } = true;

    /// <summary>执行 <c>ffmpeg -hide_banner -encoders</c> 探测 libmp3lame；探测异常按不可用处理。</summary>
    public bool Probe(string ffmpegPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                ArgumentList = { "-hide_banner", "-encoders" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                Available = false;
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            Available = process.ExitCode == 0 && stdout.Contains("libmp3lame", StringComparison.Ordinal);
        }
        catch
        {
            // ffmpeg 缺失/不可执行：降级 wav，由启动日志告警
            Available = false;
        }

        return Available;
    }
}

/// <summary>
/// 基于 ffmpeg 外部进程的音频片段抽取：<c>-ss</c>/<c>-t</c> 前置为输入侧 seek（长录播秒开、
/// accurate_seek 精确到采样），<c>pipe:1</c> 直写输出流不落盘；进程封装沿用 <see cref="IRecordingProcessFactory"/>
/// 口径（stderr 泵丢弃、取消/释放时 kill 并回收）。
/// </summary>
public sealed class AudioClipExtractor(
    RecordingOptions options,
    IRecordingProcessFactory processFactory,
    Mp3EncoderSupport mp3Support,
    ILogger<AudioClipExtractor> logger) : IAudioClipExtractor
{
    public string ContentType => mp3Support.Available ? "audio/mpeg" : "audio/wav";

    /// <summary>ffmpeg 参数构造（纯函数口径，供单测断言顺序与降级形态）。</summary>
    public IReadOnlyList<string> BuildArguments(string fullPath, long startMs, long endMs)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "error",
            // -ss/-t 置于 -i 之前：输入 seek，小时级文件秒开
            "-ss",
            FormatSeconds(startMs),
            "-t",
            FormatSeconds(endMs - startMs),
            "-i",
            fullPath,
            "-vn",
            "-ac",
            "1",
            "-ar",
            "44100",
        };

        if (mp3Support.Available)
        {
            args.AddRange(["-b:a", "96k", "-f", "mp3"]);
        }
        else
        {
            // libmp3lame 缺失：降级 wav，接口语义与参数不变
            args.AddRange(["-f", "wav"]);
        }

        args.Add(FfmpegProcessBuilder.AudioPipe);
        return args;
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
                "Audio clip extraction failed: file={FullPath} startMs={StartMs} endMs={EndMs} exitCode={ExitCode}",
                fullPath, startMs, endMs, exitCode);
            throw new AudioClipFailedException();
        }

        // exitCode != 0 但已写出字节：响应头已按 200 发出，就地中止交由客户端报播放失败

        await output.FlushAsync(ct);
    }

    private static string FormatSeconds(long ms) =>
        (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}
