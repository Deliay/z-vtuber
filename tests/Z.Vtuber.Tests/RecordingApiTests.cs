using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    [Fact]
    public void ListRecordings_NoDirectory_ReturnsEmptyArray()
    {
        var controller = CreateController(new AudioStreamHub(1024));

        var result = controller.ListRecordings(555);

        var ok = Assert.IsType<OkObjectResult>(result);
        var files = ok.Value!.GetType().GetProperty("files")!.GetValue(ok.Value) as string[];
        Assert.NotNull(files);
        Assert.Empty(files!);
    }

    [Fact]
    public void ListRecordings_ReturnsFlvFiles()
    {
        Directory.CreateDirectory(RoomDir(556));
        File.WriteAllText(Path.Combine(RoomDir(556), "[RAW][556][主播] 2026-09-13-a.flv"), "x");
        File.WriteAllText(Path.Combine(RoomDir(556), "[AV1][556][主播] 2026-09-13-a.flv"), "x");
        File.WriteAllText(Path.Combine(RoomDir(556), "ignore.txt"), "x");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = controller.ListRecordings(556);

        var ok = Assert.IsType<OkObjectResult>(result);
        var files = (ok.Value!.GetType().GetProperty("files")!.GetValue(ok.Value) as string[])!.ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.EndsWith(".flv", f));
    }

    [Fact]
    public void Download_ExistingFile_ReturnsPhysicalFile()
    {
        Directory.CreateDirectory(RoomDir(557));
        var path = Path.Combine(RoomDir(557), "a.flv");
        File.WriteAllText(path, "data");

        var controller = CreateController(new AudioStreamHub(1024));
        var result = controller.DownloadRecording(557, "a.flv");

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal("a.flv", file.FileDownloadName);
    }

    [Theory]
    [InlineData("../secret.flv")]
    [InlineData("a/b.flv")]
    [InlineData("a\\b.flv")]
    public void Download_UnsafeFileName_ReturnsBadRequest(string fileName)
    {
        var controller = CreateController(new AudioStreamHub(1024));

        var result = controller.DownloadRecording(558, fileName);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Download_MissingFile_ReturnsNotFound()
    {
        var controller = CreateController(new AudioStreamHub(1024));

        var result = controller.DownloadRecording(559, "missing.flv");

        Assert.IsType<NotFoundResult>(result);
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
