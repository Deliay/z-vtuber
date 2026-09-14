using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Controllers;

namespace Z.Vtuber.Tests;

public class RecordingApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly string _storageDir =
        Path.Combine(Path.GetTempPath(), "z-vtuber-rec-api-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingController CreateController(AudioStreamHub hub)
    {
        var controller = new RecordingController(
            new RecordingStatusRepository(fixture.Database),
            hub,
            new RecordingOptions { StorageDir = _storageDir });
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private string RoomDir(long roomId) => Path.Combine(_storageDir, roomId.ToString());

    /// <summary>
    /// 以 MVC 默认的 PhysicalFileResult 执行器落地结果，使框架原生 Range 处理（206/416/Accept-Ranges）得以在测试中生效。
    /// </summary>
    private static async Task ExecuteAsync(ControllerBase controller, IActionResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActionResultExecutor<PhysicalFileResult>, PhysicalFileResultExecutor>();
        using var provider = services.BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        controller.HttpContext.Request.Method = HttpMethods.Get;
        await result.ExecuteResultAsync(new ActionContext(
            controller.HttpContext,
            controller.RouteData ?? new RouteData(),
            new ActionDescriptor()));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)i).ToArray();

    private static JsonElement OkJson(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private Task SeedRecordingAsync(long roomId, TimeSpan age, params string[] files) =>
        new RecordingStatusRepository(fixture.Database).UpsertAsync(new RecordingStatusRecord
        {
            RoomId = roomId,
            State = "recording",
            HostName = "host",
            SessionTitle = "title",
            Files = [.. files],
            UpdatedAt = DateTime.UtcNow - age,
        });

    [Fact]
    public async Task ListRecordings_NoDirectory_ReturnsEmptyArray()
    {
        var controller = CreateController(new AudioStreamHub(1024));

        var result = await controller.ListRecordings(555, CancellationToken.None);

        var json = OkJson(result);
        Assert.Empty(json.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task ListRecordings_ReturnsFileMetadata()
    {
        Directory.CreateDirectory(RoomDir(556));
        File.WriteAllText(Path.Combine(RoomDir(556), "b.flv"), "hello");
        File.WriteAllText(Path.Combine(RoomDir(556), "a.flv"), "hi");
        File.WriteAllText(Path.Combine(RoomDir(556), "ignore.txt"), "x");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.ListRecordings(556, CancellationToken.None);

        var json = OkJson(result);
        var files = json.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);

        Assert.Equal("a.flv", files[0].GetProperty("name").GetString());
        Assert.Equal(2, files[0].GetProperty("size").GetInt64());
        Assert.Equal("b.flv", files[1].GetProperty("name").GetString());
        Assert.Equal(5, files[1].GetProperty("size").GetInt64());

        var createdAt = files[0].GetProperty("createdAt").GetDateTime();
        Assert.True(createdAt > DateTime.UtcNow.AddMinutes(-5) && createdAt <= DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task ListRecordings_ExcludesRecordingFile()
    {
        Directory.CreateDirectory(RoomDir(563));
        File.WriteAllText(Path.Combine(RoomDir(563), "active.flv"), "x");
        File.WriteAllText(Path.Combine(RoomDir(563), "finished.flv"), "x");
        await SeedRecordingAsync(563, TimeSpan.Zero, "active.flv");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.ListRecordings(563, CancellationToken.None);

        var names = OkJson(result).GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(["finished.flv"], names);
    }

    [Fact]
    public async Task ListRecordings_StaleRecordingStatus_IncludesFile()
    {
        Directory.CreateDirectory(RoomDir(564));
        File.WriteAllText(Path.Combine(RoomDir(564), "active.flv"), "x");
        await SeedRecordingAsync(564, TimeSpan.FromMinutes(10), "active.flv");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.ListRecordings(564, CancellationToken.None);

        var names = OkJson(result).GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(["active.flv"], names);
    }

    [Fact]
    public async Task Download_ExistingFile_ReturnsPhysicalFile()
    {
        Directory.CreateDirectory(RoomDir(557));
        var path = Path.Combine(RoomDir(557), "a.flv");
        File.WriteAllText(path, "data");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.DownloadRecording(557, "a.flv", CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal("a.flv", file.FileDownloadName);
    }

    [Theory]
    [InlineData("../secret.flv")]
    [InlineData("a/b.flv")]
    [InlineData("a\\b.flv")]
    public async Task Download_UnsafeFileName_ReturnsBadRequest(string fileName)
    {
        var controller = CreateController(new AudioStreamHub(1024));

        var result = await controller.DownloadRecording(558, fileName, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Download_MissingFile_ReturnsNotFound()
    {
        var controller = CreateController(new AudioStreamHub(1024));

        var result = await controller.DownloadRecording(559, "missing.flv", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Download_RecordingFile_ReturnsBadRequest()
    {
        Directory.CreateDirectory(RoomDir(565));
        File.WriteAllText(Path.Combine(RoomDir(565), "active.flv"), "x");
        await SeedRecordingAsync(565, TimeSpan.Zero, "active.flv");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.DownloadRecording(565, "active.flv", CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value) as string;
        Assert.Contains("recording", error);
    }

    [Fact]
    public async Task Download_AfterRecordingEnded_ReturnsFile()
    {
        Directory.CreateDirectory(RoomDir(566));
        File.WriteAllText(Path.Combine(RoomDir(566), "done.flv"), "x");
        await new RecordingStatusRepository(fixture.Database).UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 566,
            State = "not_live",
            Files = ["done.flv"],
            UpdatedAt = DateTime.UtcNow,
        });

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.DownloadRecording(566, "done.flv", CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
    }

    private async Task<(HttpContext Context, IActionResult Result)> DownloadWithRangeAsync(
        long roomId, string fileName, int totalBytes, string? range)
    {
        Directory.CreateDirectory(RoomDir(roomId));
        await File.WriteAllBytesAsync(Path.Combine(RoomDir(roomId), fileName), Bytes(totalBytes));

        var controller = CreateController(new AudioStreamHub(1024));
        controller.HttpContext.Response.Body = new MemoryStream();
        if (range is not null)
        {
            controller.HttpContext.Request.Headers.Range = range;
        }

        var result = await controller.DownloadRecording(roomId, fileName, CancellationToken.None);
        await ExecuteAsync(controller, result);
        return (controller.HttpContext, result);
    }

    [Fact]
    public async Task Download_RangePrefix_Returns206PartialContent()
    {
        var (context, _) = await DownloadWithRangeAsync(570, "a.flv", 200, "bytes=0-99");

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 0-99/200", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(Bytes(200)[..100], await ReadBodyAsync(context));
    }

    [Fact]
    public async Task Download_RangeOpenEnded_ReturnsTailToEndOfFile()
    {
        var (context, _) = await DownloadWithRangeAsync(571, "a.flv", 200, "bytes=150-");

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 150-199/200", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(Bytes(200)[150..], await ReadBodyAsync(context));
    }

    [Fact]
    public async Task Download_RangeSuffix_ReturnsLastBytes()
    {
        var (context, _) = await DownloadWithRangeAsync(572, "a.flv", 200, "bytes=-50");

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 150-199/200", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(Bytes(200)[150..], await ReadBodyAsync(context));
    }

    [Fact]
    public async Task Download_RangeBeyondEnd_Returns416()
    {
        var (context, _) = await DownloadWithRangeAsync(573, "a.flv", 200, "bytes=200-");

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
        Assert.Equal("bytes */200", context.Response.Headers.ContentRange.ToString());
    }

    [Fact]
    public async Task Download_NoRange_Returns200FullFileWithAcceptRanges()
    {
        var (context, _) = await DownloadWithRangeAsync(574, "a.flv", 200, null);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(Bytes(200), await ReadBodyAsync(context));
    }

    [Fact]
    public async Task Download_UnsafeFileName_WithRange_StillBadRequest()
    {
        var controller = CreateController(new AudioStreamHub(1024));
        controller.HttpContext.Request.Headers.Range = "bytes=0-99";

        var result = await controller.DownloadRecording(575, "../secret.flv", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Download_RecordingFile_WithRange_StillBadRequest()
    {
        Directory.CreateDirectory(RoomDir(576));
        await File.WriteAllBytesAsync(Path.Combine(RoomDir(576), "active.flv"), Bytes(200));
        await SeedRecordingAsync(576, TimeSpan.Zero, "active.flv");

        var controller = CreateController(new AudioStreamHub(1024));
        controller.HttpContext.Request.Headers.Range = "bytes=0-99";

        var result = await controller.DownloadRecording(576, "active.flv", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetStatus_ReturnsItems()
    {
        await new RecordingStatusRepository(fixture.Database).UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 560,
            State = "recording",
            HostName = "host",
            SessionTitle = "title",
            Files = ["a.flv"],
            UpdatedAt = DateTime.UtcNow,
        });

        var controller = CreateController(new AudioStreamHub(1024));
        var result = await controller.GetStatus(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task StreamAudio_NotStreaming_Returns404()
    {
        var controller = CreateController(new AudioStreamHub(1024));

        await controller.StreamAudio(561, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
    }

    [Fact]
    public async Task StreamAudio_Recording_StreamsAudioChunks()
    {
        var hub = new AudioStreamHub(1024);
        hub.MarkStreaming(562);
        var controller = CreateController(hub);
        controller.HttpContext.Response.Body = new MemoryStream();

        var streamTask = controller.StreamAudio(562, CancellationToken.None);

        hub.Publish(562, [1, 2, 3, 4]);
        hub.MarkStopped(562);
        await streamTask;

        Assert.Equal("video/x-flv", controller.Response.ContentType);
        controller.Response.Body.Position = 0;
        var buffer = new byte[4];
        var read = await controller.Response.Body.ReadAsync(buffer);
        Assert.Equal(4, read);
        Assert.Equal([1, 2, 3, 4], buffer);
    }
}
