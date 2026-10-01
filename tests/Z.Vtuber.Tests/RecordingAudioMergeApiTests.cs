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
using Z.Vtuber.Recording.Contracts;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

/// <summary>
/// 多片段合并端点：护栏（空 items/超上限/区间非法/总时长超限）、404、录制中 400、真实 ffmpeg 合并 200 / 失败 500。
/// 无 ffmpeg 环境按 FfmpegRecordingSmokeTests 模式跳过真实 ffmpeg 用例。
/// </summary>
public class RecordingAudioMergeApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly string _storageDir =
        Path.Combine(Path.GetTempPath(), "z-vtuber-rec-audio-merge-tests", Guid.NewGuid().ToString("N"));

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

    private RecordingOptions Options(int? maxItems = null, int? maxTotalMs = null)
    {
        var options = new RecordingOptions { StorageDir = _storageDir };
        if (maxItems.HasValue)
        {
            options.AudioMergeMaxItems = maxItems.Value;
        }

        if (maxTotalMs.HasValue)
        {
            options.AudioMergeMaxTotalDurationMs = maxTotalMs.Value;
        }

        return options;
    }

    private RecordingController CreateController(RecordingOptions options)
    {
        var mp3 = new Mp3EncoderSupport();
        var extractor = new AudioClipExtractor(
            options, new FfmpegRecordingProcessFactory(NullLogger<FfmpegRecordingProcessFactory>.Instance),
            mp3, NullLogger<AudioClipExtractor>.Instance);
        var merger = new AudioClipMerger(
            options, new FfmpegRecordingProcessFactory(NullLogger<FfmpegRecordingProcessFactory>.Instance),
            mp3, NullLogger<AudioClipMerger>.Instance);

        var controller = new RecordingController(
            new RecordingStatusRepository(fixture.Database),
            new RecordingRepository(fixture.Database),
            new AudioStreamHub(1024),
            options,
            extractor,
            merger);
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

    private static RecordingAudioMergeRequest Request(params RecordingAudioMergeItem[] items) =>
        new() { Items = [.. items] };

    private static RecordingAudioMergeItem Item(long roomId, string fileName, long startMs, long endMs) =>
        new() { RoomId = roomId, FileName = fileName, StartMs = startMs, EndMs = endMs };

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
        Assert.True(body.Length > 0, "expected non-empty merged audio body");

        var outDir = Path.Combine(Path.GetTempPath(), "z-vtuber-audio-merge-out");
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

    private static (int Status, string Error) ErrorOf(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        var error = JsonSerializer.SerializeToElement(objectResult.Value).GetProperty("error").GetString()!;
        return (objectResult.StatusCode ?? 0, error);
    }

    [Fact]
    public async Task Merge_EmptyItems_Returns400()
    {
        var result = await CreateController(Options())
            .MergeRecordingAudioClips(new RecordingAudioMergeRequest(), CancellationToken.None);

        var (status, error) = ErrorOf(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("items is required", error);
    }

    [Fact]
    public async Task Merge_NullItems_Returns400()
    {
        var result = await CreateController(Options())
            .MergeRecordingAudioClips(null, CancellationToken.None);

        Assert.Equal("items is required", ErrorOf(result).Error);
    }

    [Fact]
    public async Task Merge_TooManyItems_Returns400()
    {
        var request = Request(
            Item(1, "a.flv", 0, 100),
            Item(1, "b.flv", 0, 100));

        var result = await CreateController(Options(maxItems: 1))
            .MergeRecordingAudioClips(request, CancellationToken.None);

        Assert.Equal("too many items", ErrorOf(result).Error);
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(2000, 1000)]
    public async Task Merge_InvalidRange_Returns400(long startMs, long endMs)
    {
        var result = await CreateController(Options())
            .MergeRecordingAudioClips(Request(Item(1, "a.flv", startMs, endMs)), CancellationToken.None);

        Assert.Equal("invalid time range", ErrorOf(result).Error);
    }

    [Fact]
    public async Task Merge_SingleClipOverMaxDuration_Returns400()
    {
        var options = Options();
        options.AudioClipMaxDurationMs = 1000;

        var result = await CreateController(options)
            .MergeRecordingAudioClips(Request(Item(1, "a.flv", 0, 1500)), CancellationToken.None);

        Assert.Equal("invalid time range", ErrorOf(result).Error);
    }

    [Fact]
    public async Task Merge_TotalDurationTooLong_Returns400()
    {
        var result = await CreateController(Options(maxTotalMs: 2500))
            .MergeRecordingAudioClips(
                Request(Item(1, "a.flv", 0, 1500), Item(1, "b.flv", 0, 1500)), CancellationToken.None);

        Assert.Equal("total duration too long", ErrorOf(result).Error);
    }

    [Fact]
    public async Task Merge_MissingFile_Returns404FileNotFound()
    {
        var result = await CreateController(Options())
            .MergeRecordingAudioClips(Request(Item(1, "missing.flv", 0, 1000)), CancellationToken.None);

        var (status, error) = ErrorOf(result);
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("file not found", error);
    }

    [Theory]
    [InlineData("../secret.flv")]
    [InlineData("a/b.flv")]
    public async Task Merge_UnsafeFileName_Returns404(string fileName)
    {
        var result = await CreateController(Options())
            .MergeRecordingAudioClips(Request(Item(1, fileName, 0, 1000)), CancellationToken.None);

        Assert.Equal("file not found", ErrorOf(result).Error);
    }

    [Fact]
    public async Task Merge_RecordingFile_Returns400RecordingInProgress()
    {
        Directory.CreateDirectory(RoomDir(801));
        await File.WriteAllTextAsync(Path.Combine(RoomDir(801), "active.flv"), "x");
        await SeedRecordingAsync(801, "active.flv");

        var result = await CreateController(Options())
            .MergeRecordingAudioClips(Request(Item(801, "active.flv", 0, 1000)), CancellationToken.None);

        var (status, error) = ErrorOf(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("recording in progress", error);
    }

    [Fact]
    public async Task Merge_ValidItems_Returns200MpegWithCombinedDuration()
    {
        if (!FfmpegAvailable() || !FfprobeAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(802));
        await CreateSourceAudioAsync(Path.Combine(RoomDir(802), "a.flv"), 3);
        await CreateSourceAudioAsync(Path.Combine(RoomDir(802), "b.flv"), 3);

        var controller = CreateController(Options());
        var result = await controller.MergeRecordingAudioClips(
            Request(Item(802, "a.flv", 0, 1000), Item(802, "b.flv", 0, 1000)), CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("audio/mpeg", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());

        var duration = await ProbeBodyDurationAsync(controller.HttpContext, "merge.mp3");
        Assert.InRange(duration, 1.5, 2.5);
    }

    [Fact]
    public async Task Merge_CorruptFile_Returns500AudioMergeFailed()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        Directory.CreateDirectory(RoomDir(803));
        await File.WriteAllBytesAsync(
            Path.Combine(RoomDir(803), "corrupt.flv"), RandomNumberGenerator.GetBytes(4096));

        var result = await CreateController(Options())
            .MergeRecordingAudioClips(Request(Item(803, "corrupt.flv", 0, 1000)), CancellationToken.None);

        var (status, error) = ErrorOf(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("audio merge failed", error);
    }
}
