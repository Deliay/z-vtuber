using System.Diagnostics;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Recording.Live;

public interface IStreamBitrateProbe
{
    Task<int?> ProbeAsync(LiveStreamCandidate stream, string cookie, CancellationToken ct = default);
}

/// <summary>
/// 用 ffprobe 短时探测流的视频码率（kbps）。探测失败返回 null，由调用方回退默认码率。
/// </summary>
public sealed class FfprobeStreamBitrateProbe(RecordingOptions options, ILogger<FfprobeStreamBitrateProbe> logger)
    : IStreamBitrateProbe
{
    public async Task<int?> ProbeAsync(LiveStreamCandidate stream, string cookie, CancellationToken ct = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = options.FfprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=bit_rate");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");

            if (FfmpegProcessBuilder.RequiresHeaders(stream))
            {
                startInfo.ArgumentList.Add("-headers");
                startInfo.ArgumentList.Add(
                    $"Cookie: {cookie}\r\nReferer: {options.Referer}\r\nUser-Agent: {options.UserAgent}\r\n");
            }

            startInfo.ArgumentList.Add(stream.Url);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var line = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (line is null || !long.TryParse(line, out var bitsPerSecond) || bitsPerSecond <= 0)
            {
                return null;
            }

            return (int)Math.Round(bitsPerSecond / 1000.0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Bitrate probe failed for {Url}", stream.Url);
            return null;
        }
    }
}
