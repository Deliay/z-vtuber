using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Contracts;
using Z.Vtuber.Recording.Controllers;

namespace Z.Vtuber.Tests;

public class RecordingFileStatsTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>
{
    private readonly string _storageDir =
        Path.Combine(Path.GetTempPath(), "z-vtuber-file-stats-tests", Guid.NewGuid().ToString("N"));

    private RecordingController CreateController()
    {
        var controller = new RecordingController(
            new RecordingStatusRepository(fixture.Database),
            new RecordingRepository(fixture.Database),
            new AudioStreamHub(1024),
            new RecordingOptions { StorageDir = _storageDir });
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private void WriteRoomFile(long roomId, string fileName, int bytes)
    {
        var dir = Path.Combine(_storageDir, roomId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), Encoding.UTF8.GetBytes(new string('x', bytes)));
    }

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public void ExistingFile_ReturnsExistsAndActualSize()
    {
        WriteRoomFile(1001, "a.flv", 7);

        var json = Body(CreateController().GetFileStats(new RecordingFileStatsRequest
        {
            Items = [new RecordingFileStatsRoomRequest { RoomId = 1001, Files = ["a.flv"] }],
        }));

        var file = json.GetProperty("items")[0].GetProperty("files")[0];
        Assert.True(file.GetProperty("exists").GetBoolean());
        Assert.Equal(7, file.GetProperty("size").GetInt64());
    }

    [Fact]
    public void MissingFileOrRoom_ReturnsNotExists()
    {
        var json = Body(CreateController().GetFileStats(new RecordingFileStatsRequest
        {
            Items =
            [
                new RecordingFileStatsRoomRequest { RoomId = 1002, Files = ["missing.flv"] },
                new RecordingFileStatsRoomRequest { RoomId = 9999, Files = ["whatever.flv"] },
            ],
        }));

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        foreach (var file in items.SelectMany(i => i.GetProperty("files").EnumerateArray()))
        {
            Assert.False(file.GetProperty("exists").GetBoolean());
            Assert.Equal(JsonValueKind.Null, file.GetProperty("size").ValueKind);
        }
    }

    [Theory]
    [InlineData("../secret.flv")]
    [InlineData("sub/inner.flv")]
    [InlineData("sub\\inner.flv")]
    [InlineData("")]
    public void PathTraversalOrInvalidName_TreatedAsNotExists(string fileName)
    {
        var json = Body(CreateController().GetFileStats(new RecordingFileStatsRequest
        {
            Items = [new RecordingFileStatsRoomRequest { RoomId = 1003, Files = [fileName] }],
        }));

        var file = json.GetProperty("items")[0].GetProperty("files")[0];
        Assert.False(file.GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("size").ValueKind);
    }

    [Fact]
    public void ItemsBeyondLimit_Returns400()
    {
        var items = Enumerable.Range(0, RecordingController.MaxFileStatsItems + 1)
            .Select(i => new RecordingFileStatsRoomRequest { RoomId = i, Files = [] })
            .ToList();

        var result = CreateController().GetFileStats(new RecordingFileStatsRequest { Items = items });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void MissingBodyOrItems_Returns400()
    {
        Assert.IsType<BadRequestObjectResult>(CreateController().GetFileStats(null));
        Assert.IsType<BadRequestObjectResult>(
            CreateController().GetFileStats(new RecordingFileStatsRequest { Items = null }));
    }
}
