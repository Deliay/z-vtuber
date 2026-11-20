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
using Z.Vtuber.Recording.Video;

namespace Z.Vtuber.Tests;

/// <summary>
/// 录播视频切片端点（Plan 2.2）：lavfi testsrc2 + sine 生成 6s H.264/AAC FLV →
/// 按区间 -c copy 输出 fMP4；无 ffmpeg/ffprobe 环境按既有音频用例模式跳过。
/// </summary>
public class RecordingVideoClipTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly string _storageDir =
        Path.Combine(Path.GetTempPath(), "z-vtuber-rec-video-clip-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static bool ToolAvailable(string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = name,
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

    private static bool FfmpegAvailable() => ToolAvailable("ffmpeg");

    private static bool FfprobeAvailable() => ToolAvailable("ffprobe");

    private VideoClipExtractor CreateExtractor(IRecordingProcessFactory? factory = null) =>
        new(
            new RecordingOptions { StorageDir = _storageDir },
            factory ?? new FfmpegRecordingProcessFactory(NullLogger<FfmpegRecordingProcessFactory>.Instance),
            NullLogger<VideoClipExtractor>.Instance);

    private RecordingController CreateController(
        int? maxDurationMs = null,
        IVideoClipExtractor? extractor = null)
    {
        var options = new RecordingOptions { StorageDir = _storageDir };
        if (maxDurationMs.HasValue)
        {
            options.VideoClipMaxDurationMs = maxDurationMs.Value;
        }

        var controller = new RecordingController(
            new RecordingStatusRepository(fixture.Database),
            new RecordingRepository(fixture.Database),
            new AudioStreamHub(1024),
            options,
            null,
            null,
            extractor ?? CreateExtractor());
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

    /// <summary>lavfi 合成 6s 320x240 H.264/AAC FLV，GOP 约 2s，便于关键帧吸附断言。</summary>
    private static async Task CreateSourceVideoAsync(string path, double seconds = 6)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", $"testsrc2=size=320x240:rate=24:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
                "-f", "lavfi",
                "-i", $"sine=frequency=440:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "48",
                "-c:a", "aac", "-shortest", "-f", "flv", path,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        var stderr = process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stderr;
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var body = new MemoryStream();
        await context.Response.Body.CopyToAsync(body);
        return body.ToArray();
    }

    private static async Task<double> ProbeDurationSecondsAsync(byte[] content)
    {
        Assert.True(content.Length > 0, "expected non-empty video clip body");
        var outDir = Path.Combine(Path.GetTempPath(), "z-vtuber-video-clip-out");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(outPath, content);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffprobe",
            ArgumentList =
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                outPath,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        var stdout = process!.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        Assert.Equal(0, process.ExitCode);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private static string ErrorOf(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result).Value)
            .GetProperty("error")
            .GetString()!;

    [Fact]
    public async Task Clip_ValidRange_Returns200Mp4WithAttachmentAndClipDuration()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        const string fileName = "[RAW][801][主播A] 2026-10-21-测试场次.flv";
        Directory.CreateDirectory(RoomDir(801));
        await CreateSourceVideoAsync(Path.Combine(RoomDir(801), fileName), 6);

        var controller = CreateController();
        var result = await controller.DownloadRecordingClip(801, fileName, 1000, 4000, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("video/mp4", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Contains("attachment", disposition);
        Assert.Contains("filename*=utf-8''", disposition.ToLowerInvariant());
        Assert.Contains(".mp4", disposition);

        var body = await ReadBodyAsync(controller.HttpContext);
        Assert.True(body.Length > 0);
        // fMP4：ftyp 开头、moov 内置；ffprobe 可解析且时长与请求区间接近（关键帧吸附允许秒级偏差）
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(body, 4, 4));
        var duration = await ProbeDurationSecondsAsync(body);
        Assert.InRange(duration, 2.5, 4.5);
    }

    [Fact]
    public async Task Clip_EndBeyondFileLength_OutputsToEOF()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(802));
        await CreateSourceVideoAsync(Path.Combine(RoomDir(802), "clip.flv"), 6);

        var controller = CreateController();
        var result = await controller.DownloadRecordingClip(802, "clip.flv", 4100, 60000, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);

        var duration = await ProbeDurationSecondsAsync(await ReadBodyAsync(controller.HttpContext));
        // 6s 文件从 4s 抽到 60s：自然输出到结尾 ≈ 2s，不报错
        Assert.InRange(duration, 1.2, 3.2);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1000L, null)]
    [InlineData(null, 2500L)]
    public async Task Clip_MissingParams_Returns400(long? startMs, long? endMs)
    {
        var result = await CreateController().DownloadRecordingClip(803, "clip.flv", startMs, endMs, CancellationToken.None);

        Assert.Equal("startMs and endMs are required", ErrorOf(result));
    }

    [Fact]
    public async Task Clip_NegativeStart_Returns400()
    {
        var result = await CreateController().DownloadRecordingClip(804, "clip.flv", -1, 1000, CancellationToken.None);

        Assert.Equal("startMs must be >= 0", ErrorOf(result));
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(2500, 1000)]
    public async Task Clip_EndNotGreaterThanStart_Returns400(long startMs, long endMs)
    {
        var result = await CreateController().DownloadRecordingClip(805, "clip.flv", startMs, endMs, CancellationToken.None);

        Assert.Equal("endMs must be greater than startMs", ErrorOf(result));
    }

    [Fact]
    public async Task Clip_DurationOverConfiguredLimit_Returns400()
    {
        var result = await CreateController(maxDurationMs: 1000)
            .DownloadRecordingClip(806, "clip.flv", 0, 1500, CancellationToken.None);

        Assert.Equal("clip duration must be <= 1000 ms", ErrorOf(result));
    }

    [Fact]
    public async Task Clip_MissingFile_Returns404()
    {
        var result = await CreateController().DownloadRecordingClip(807, "missing.flv", 0, 1000, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Clip_RecordingFile_Returns400()
    {
        Directory.CreateDirectory(RoomDir(808));
        await File.WriteAllTextAsync(Path.Combine(RoomDir(808), "active.flv"), "x");
        await SeedRecordingAsync(808, "active.flv");

        var result = await CreateController().DownloadRecordingClip(808, "active.flv", 0, 1000, CancellationToken.None);

        Assert.Contains("recording", ErrorOf(result));
    }

    [Theory]
    [InlineData("../secret.flv")]
    [InlineData("a/b.flv")]
    [InlineData("a\\b.flv")]
    public async Task Clip_UnsafeFileName_Returns400(string fileName)
    {
        var result = await CreateController().DownloadRecordingClip(809, fileName, 0, 1000, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Clip_PathTraversal_Returns404()
    {
        var result = await CreateController().DownloadRecordingClip(810, "..", 0, 1000, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Clip_CorruptFile_Returns500VideoClipFailed()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(811));
        await File.WriteAllBytesAsync(
            Path.Combine(RoomDir(811), "corrupt.flv"),
            RandomNumberGenerator.GetBytes(4096));

        var controller = CreateController();
        var result = await controller.DownloadRecordingClip(811, "corrupt.flv", 0, 1000, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, status.StatusCode);
        Assert.Equal(
            "video clip failed",
            JsonSerializer.SerializeToElement(status.Value).GetProperty("error").GetString());
        Assert.Null(controller.Response.ContentType);
        Assert.False(controller.Response.Headers.ContainsKey("Content-Disposition"));
    }
}
