using System.Diagnostics;
using System.Globalization;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Recording.Audio;

/// <summary>合并输入片段：已解析的录播文件全路径 + 起止毫秒。</summary>
public sealed record AudioMergeClip(string FullPath, long StartMs, long EndMs);

/// <summary>多片段合并失败（ffmpeg 非零退出且未写出任何字节）。</summary>
public sealed class AudioMergeFailedException() : Exception("audio merge failed")
{
}

/// <summary>多片段音频合并：单进程多输入 concat 输出单 mp3（流式 pipe，不落盘）。</summary>
public interface IAudioClipMerger
{
    /// <summary>合并输出 Content-Type：audio/mpeg（libmp3lame 可用）或 audio/wav（降级，语义不变）。</summary>
    string ContentType { get; }

    /// <summary>合并失败（未写出任何字节）抛 <see cref="AudioMergeFailedException"/>。</summary>
    Task MergeAsync(IReadOnlyList<AudioMergeClip> items, Stream output, CancellationToken ct);
}

/// <summary>
/// 基于 ffmpeg 外部进程的多片段合并：每个输入一组 <c>-ss</c>/<c>-t</c>（输入侧 seek，置于 <c>-i</c> 前）、
/// <c>concat=n=N:v=0:a=1</c> filter 串联，统一转单声道 44.1kHz 后 <c>pipe:1</c> 直写输出流不落盘。
/// 复用 <see cref="Mp3EncoderSupport"/>：缺 libmp3lame 时降级 wav。取消时 kill 进程并回收。
/// </summary>
public sealed class AudioClipMerger(
    RecordingOptions options,
    IRecordingProcessFactory processFactory,
    Mp3EncoderSupport mp3Support,
    ILogger<AudioClipMerger> logger) : IAudioClipMerger
{
    public string ContentType => mp3Support.Available ? "audio/mpeg" : "audio/wav";

    /// <summary>ffmpeg 参数构造（纯函数口径，供单测断言顺序与降级形态）。</summary>
    public IReadOnlyList<string> BuildArguments(IReadOnlyList<AudioMergeClip> items)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        // 每输入一组 -ss/-t（输入 seek，小时级文件秒开）+ -i。
        foreach (var item in items)
        {
            args.Add("-ss");
            args.Add(FormatSeconds(item.StartMs));
            args.Add("-t");
            args.Add(FormatSeconds(item.EndMs - item.StartMs));
            args.Add("-i");
            args.Add(item.FullPath);
        }

        var inputs = string.Concat(Enumerable.Range(0, items.Count).Select(i => $"[{i}:a]"));
        args.Add("-filter_complex");
        args.Add($"{inputs}concat=n={items.Count}:v=0:a=1[out]");
        args.Add("-map");
        args.Add("[out]");
        args.AddRange(["-vn", "-ac", "1", "-ar", "44100"]);

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

    public async Task MergeAsync(IReadOnlyList<AudioMergeClip> items, Stream output, CancellationToken ct)
    {
        var command = new FfmpegCommand(options.FfmpegPath, BuildArguments(items));
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
                "Audio merge failed: items={Items} exitCode={ExitCode}", items.Count, exitCode);
            throw new AudioMergeFailedException();
        }

        // exitCode != 0 但已写出字节：响应头已按 200 发出，就地中止交由客户端报播放失败

        await output.FlushAsync(ct);
    }

    private static string FormatSeconds(long ms) =>
        (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}
