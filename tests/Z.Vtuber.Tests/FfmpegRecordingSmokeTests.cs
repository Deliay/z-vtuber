using System.Diagnostics;
using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

public class FfmpegRecordingSmokeTests
{
    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool Av1NvencAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-hide_banner", "-loglevel", "error",
                    "-f", "lavfi", "-i", "testsrc=duration=1:size=320x240:rate=15",
                    "-c:v", "av1_nvenc", "-f", "null", "-",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process!.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task MiniRecording_ProducesRawFileAndAudioPipe()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "z-vtuber-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.flv");
        var rawFile = Path.Combine(dir, "raw.flv");

        // 生成 2s 测试源（视频+音频）
        var create = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=15",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-c:a", "aac", "-t", "2", source,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        await create!.WaitForExitAsync();
        Assert.Equal(0, create.ExitCode);

        var command = FfmpegProcessBuilder.Build(
            new LiveStreamCandidate(source, "local", "flv", "avc"),
            rawFile,
            av1FilePath: null,
            cookie: string.Empty,
            referer: "https://live.bilibili.com/",
            userAgent: "smoke",
            rawBitrateKbps: 1000,
            defaultBitrateKbps: 4000,
            av1Enabled: false);

        var factory = new FfmpegRecordingProcessFactory(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegRecordingProcessFactory>.Instance);
        await using var process = factory.Start(command);

        using var audio = new MemoryStream();
        var pump = process.StandardOutput.CopyToAsync(audio);
        var exitCode = await process.WaitForExitAsync(CancellationToken.None);
        await pump;

        Assert.Equal(0, exitCode);
        Assert.True(new FileInfo(rawFile).Length > 0);
        Assert.True(audio.Length > 0);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task MiniRecording_WithAv1Nvenc_ProducesSecondFile_WhenAvailable()
    {
        if (!FfmpegAvailable() || !Av1NvencAvailable())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "z-vtuber-smoke-av1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.flv");
        var rawFile = Path.Combine(dir, "raw.flv");
        var av1File = Path.Combine(dir, "av1.flv");

        var create = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=15",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-c:a", "aac", "-t", "2", source,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        await create!.WaitForExitAsync();

        var command = FfmpegProcessBuilder.Build(
            new LiveStreamCandidate(source, "local", "flv", "avc"),
            rawFile,
            av1File,
            cookie: string.Empty,
            referer: "https://live.bilibili.com/",
            userAgent: "smoke",
            rawBitrateKbps: 1000,
            defaultBitrateKbps: 4000,
            av1Enabled: true);

        var factory = new FfmpegRecordingProcessFactory(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegRecordingProcessFactory>.Instance);
        await using var process = factory.Start(command);
        var exitCode = await process.WaitForExitAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(new FileInfo(rawFile).Length > 0);
        Assert.True(new FileInfo(av1File).Length > 0);

        Directory.Delete(dir, recursive: true);
    }
}
