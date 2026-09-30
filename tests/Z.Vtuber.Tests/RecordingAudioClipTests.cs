using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Controllers;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

/// <summary>
/// 录播音频片段端点（Plan 1.1）：lavfi 生成音频 → 区间抽取 mp3 流式返回，
/// 参数/文件解析口径与下载端点一致；无 ffmpeg/ffprobe 环境按 FfmpegRecordingSmokeTests 模式跳过。
/// </summary>
public class RecordingAudioClipTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly string _storageDir =
        Path.Combine(Path.GetTempPath(), "z-vtuber-rec-audio-clip-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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

    private static bool FfprobeAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffprobe",
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

    private AudioClipExtractor CreateExtractor(Mp3EncoderSupport mp3Support) =>
        new(
            new RecordingOptions { StorageDir = _storageDir },
            new FfmpegRecordingProcessFactory(NullLogger<FfmpegRecordingProcessFactory>.Instance),
            mp3Support,
            NullLogger<AudioClipExtractor>.Instance);

    private RecordingController CreateController(int? maxDurationMs = null)
    {
        var options = new RecordingOptions { StorageDir = _storageDir };
        if (maxDurationMs.HasValue)
        {
            options.AudioClipMaxDurationMs = maxDurationMs.Value;
        }

        var controller = new RecordingController(
            new RecordingStatusRepository(fixture.Database),
            new RecordingRepository(fixture.Database),
            new AudioStreamHub(1024),
            options,
            CreateExtractor(new Mp3EncoderSupport()));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private string RoomDir(long roomId) => Path.Combine(_storageDir, roomId.ToString());

    private Task SeedRecordingAsync(long roomId, params string[] files) =>
        new RecordingStatusRepository(fixture.Database).UpsertAsync(new RecordingStatusRecord
        {
            RoomId = roomId,
            State = "recording",
            HostName = "host",
            SessionTitle = "title",
            Files = [.. files],
            UpdatedAt = DateTime.UtcNow,
        });

    /// <summary>lavfi 正弦波造一段 flv 录播音频（与 FfmpegRecordingSmokeTests 同模式）。</summary>
    private static async Task CreateSourceAudioAsync(string path, double seconds)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", $"sine=frequency=440:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
                "-c:a", "aac", "-f", "flv", path,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        var stderr = process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stderr;
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<double> ProbeBodyDurationAsync(HttpContext context, string name)
    {
        context.Response.Body.Position = 0;
        using var body = new MemoryStream();
        await context.Response.Body.CopyToAsync(body);
        Assert.True(body.Length > 0, "expected non-empty audio clip body");

        var outDir = Path.Combine(Path.GetTempPath(), "z-vtuber-audio-clip-out");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{Guid.NewGuid():N}-{name}");
        await File.WriteAllBytesAsync(outPath, body.ToArray());
        return ProbeDurationSeconds(outPath);
    }

    private static double ProbeDurationSeconds(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffprobe",
            ArgumentList =
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        var stdout = process!.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        Assert.Equal(0, process.ExitCode);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private static string ErrorOf(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result).Value)
            .GetProperty("error")
            .GetString()!;

    [Fact]
    public void BuildArguments_SeeksBeforeInputAndTargetsMp3()
    {
        var args = CreateExtractor(new Mp3EncoderSupport())
            .BuildArguments("/data/1/clip.flv", 1000, 2500, AudioClipFormat.Mp3);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", "1", "-t", "1.5", "-i", "/data/1/clip.flv",
                "-vn", "-ac", "1", "-ar", "44100",
                "-b:a", "96k", "-f", "mp3", "pipe:1",
            },
            args);
    }

    [Fact]
    public void BuildArguments_WavFormat_ForcesWavEvenWhenMp3Available()
    {
        var extractor = CreateExtractor(new Mp3EncoderSupport { Available = true });

        var args = extractor.BuildArguments("/data/1/clip.flv", 0, 1000, AudioClipFormat.Wav);

        Assert.Equal("wav", args[Array.IndexOf(args.ToArray(), "-f") + 1]);
        Assert.DoesNotContain("-b:a", args);
        Assert.Equal("audio/wav", extractor.ContentType(AudioClipFormat.Wav));
    }

    [Fact]
    public void BuildArguments_WithoutMp3Encoder_DegradesToWav()
    {
        var extractor = CreateExtractor(new Mp3EncoderSupport { Available = false });

        var args = extractor.BuildArguments("/data/1/clip.flv", 0, 1000, AudioClipFormat.Mp3);

        Assert.Equal("wav", args[Array.IndexOf(args.ToArray(), "-f") + 1]);
        Assert.DoesNotContain("-b:a", args);
        Assert.Equal("audio/wav", extractor.ContentType(AudioClipFormat.Mp3));
    }

    [Fact]
    public void ContentType_Mp3WithEncoder_ReturnsMpeg()
    {
        var extractor = CreateExtractor(new Mp3EncoderSupport { Available = true });

        Assert.Equal("audio/mpeg", extractor.ContentType(AudioClipFormat.Mp3));
    }

    [Theory]
    [InlineData(null, AudioClipFormat.Mp3)]
    [InlineData("", AudioClipFormat.Mp3)]
    [InlineData("mp3", AudioClipFormat.Mp3)]
    [InlineData("MP3", AudioClipFormat.Mp3)]
    [InlineData("wav", AudioClipFormat.Wav)]
    [InlineData("WAV", AudioClipFormat.Wav)]
    public void AudioClipFormats_TryParse_AcceptsKnownFormats(string? value, AudioClipFormat expected)
    {
        Assert.True(AudioClipFormats.TryParse(value, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("ogg")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public void AudioClipFormats_TryParse_RejectsUnknownFormats(string value)
    {
        Assert.False(AudioClipFormats.TryParse(value, out _));
    }

    [Fact]
    public void Mp3EncoderProbe_MissingFfmpeg_ReturnsUnavailable()
    {
        var support = new Mp3EncoderSupport();

        Assert.False(support.Probe("ffmpeg-definitely-not-installed"));
        Assert.False(support.Available);
    }

    [Fact]
    public void Mp3EncoderProbe_WithLocalFfmpeg_DetectsMp3Encoder()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        Assert.True(new Mp3EncoderSupport().Probe("ffmpeg"));
    }

    [Fact]
    public async Task AudioClip_ValidRange_Returns200MpegWithClipDuration()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(701));
        await CreateSourceAudioAsync(Path.Combine(RoomDir(701), "clip.flv"), 5);

        var controller = CreateController();
        var result = await controller.DownloadRecordingAudioClip(701, "clip.flv", 1000, 2500, null, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("audio/mpeg", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());

        var duration = await ProbeBodyDurationAsync(controller.HttpContext, "clip.mp3");
        Assert.InRange(duration, 1.2, 1.8);
    }

    [Fact]
    public async Task AudioClip_WavFormat_Returns200WavWithClipDuration()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(714));
        await CreateSourceAudioAsync(Path.Combine(RoomDir(714), "clip.flv"), 5);

        var controller = CreateController();
        var result = await controller.DownloadRecordingAudioClip(
            714, "clip.flv", 1000, 2500, "wav", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("audio/wav", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());

        var duration = await ProbeBodyDurationAsync(controller.HttpContext, "clip.wav");
        Assert.InRange(duration, 1.2, 1.8);
    }

    [Fact]
    public async Task AudioClip_InvalidFormat_Returns400()
    {
        var controller = CreateController();

        var result = await controller.DownloadRecordingAudioClip(
            715, "clip.flv", 0, 1000, "ogg", CancellationToken.None);

        Assert.Contains("format", ErrorOf(result));
    }

    [Fact]
    public async Task AudioClip_EndBeyondFileLength_OutputsToEOF()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(702));
        await CreateSourceAudioAsync(Path.Combine(RoomDir(702), "clip.flv"), 5);

        var controller = CreateController();
        var result = await controller.DownloadRecordingAudioClip(702, "clip.flv", 4000, 60000, null, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);

        // 5s 文件从 4s 抽到 60s：自然输出到文件结尾 ≈ 1s，不报错
        var duration = await ProbeBodyDurationAsync(controller.HttpContext, "tail.mp3");
        Assert.InRange(duration, 0.5, 1.7);
    }

    [Fact]
    public async Task AudioClip_RangeHeader_Ignored_Returns200FullClip()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(703));
        await CreateSourceAudioAsync(Path.Combine(RoomDir(703), "clip.flv"), 5);

        var controller = CreateController();
        controller.HttpContext.Request.Headers.Range = "bytes=0-99";
        var result = await controller.DownloadRecordingAudioClip(703, "clip.flv", 1000, 2500, null, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.False(controller.Response.Headers.ContainsKey("Content-Range"));
        Assert.False(controller.Response.Headers.ContainsKey("Accept-Ranges"));

        var duration = await ProbeBodyDurationAsync(controller.HttpContext, "range-ignored.mp3");
        Assert.InRange(duration, 1.2, 1.8);
    }

    [Fact]
    public async Task AudioClip_CorruptFile_Returns500AudioClipFailed()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(704));
        await File.WriteAllBytesAsync(
            Path.Combine(RoomDir(704), "corrupt.flv"),
            RandomNumberGenerator.GetBytes(4096));

        var controller = CreateController();
        var result = await controller.DownloadRecordingAudioClip(704, "corrupt.flv", 0, 1000, null, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, status.StatusCode);
        Assert.Equal(
            "audio clip failed",
            JsonSerializer.SerializeToElement(status.Value).GetProperty("error").GetString());
        Assert.Null(controller.Response.ContentType);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(2500, 1000)]
    public async Task AudioClip_EndNotGreaterThanStart_Returns400(long startMs, long endMs)
    {
        var controller = CreateController();

        var result = await controller.DownloadRecordingAudioClip(705, "clip.flv", startMs, endMs, null, CancellationToken.None);

        Assert.NotEmpty(ErrorOf(result));
    }

    [Fact]
    public async Task AudioClip_NegativeStart_Returns400()
    {
        var controller = CreateController();

        var result = await controller.DownloadRecordingAudioClip(706, "clip.flv", -1, 1000, null, CancellationToken.None);

        Assert.NotEmpty(ErrorOf(result));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1000, null)]
    [InlineData(null, 2500)]
    public async Task AudioClip_MissingRangeParams_Returns400(int? startMs, int? endMs)
    {
        var controller = CreateController();

        var result = await controller.DownloadRecordingAudioClip(707, "clip.flv", startMs, endMs, null, CancellationToken.None);

        Assert.NotEmpty(ErrorOf(result));
    }

    [Fact]
    public async Task AudioClip_DurationOverDefaultLimit_Returns400()
    {
        var controller = CreateController();

        // 默认 AudioClipMaxDurationMs = 300000
        var result = await controller.DownloadRecordingAudioClip(708, "clip.flv", 0, 300001, null, CancellationToken.None);

        Assert.NotEmpty(ErrorOf(result));
    }

    [Fact]
    public async Task AudioClip_DurationOverConfiguredLimit_Returns400()
    {
        var controller = CreateController(maxDurationMs: 1000);

        var result = await controller.DownloadRecordingAudioClip(709, "clip.flv", 0, 1500, null, CancellationToken.None);

        Assert.NotEmpty(ErrorOf(result));
    }

    [Fact]
    public async Task AudioClip_MissingFile_Returns404()
    {
        var controller = CreateController();

        var result = await controller.DownloadRecordingAudioClip(710, "missing.flv", 0, 1000, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AudioClip_RecordingFile_Returns400()
    {
        Directory.CreateDirectory(RoomDir(711));
        await File.WriteAllTextAsync(Path.Combine(RoomDir(711), "active.flv"), "x");
        await SeedRecordingAsync(711, "active.flv");

        var controller = CreateController();
        var result = await controller.DownloadRecordingAudioClip(711, "active.flv", 0, 1000, null, CancellationToken.None);

        Assert.Contains("recording", ErrorOf(result));
    }

    [Theory]
    [InlineData("../secret.flv")]
    [InlineData("a/b.flv")]
    [InlineData("a\\b.flv")]
    public async Task AudioClip_UnsafeFileName_Returns400(string fileName)
    {
        var controller = CreateController();

        var result = await controller.DownloadRecordingAudioClip(712, fileName, 0, 1000, null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AudioClip_PathTraversal_Returns404()
    {
        var controller = CreateController();

        // 平文件名形式的 ".."：通过 IsSafeFileName，由解析后前缀校验兜底 404
        var result = await controller.DownloadRecordingAudioClip(713, "..", 0, 1000, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}
