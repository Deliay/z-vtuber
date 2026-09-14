using System.Text.Json;
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
