using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Controllers;

namespace Z.Vtuber.Tests;

public class CandidateRoomsTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);
    private AccountBasicInfoRepository AccountInfo => new(mongo.Database);

    private static readonly IOptions<TranscriptApiOptions> DefaultOptions =
        Options.Create(new TranscriptApiOptions());

    private RecordingsArchiveController CreateController() =>
        WithContext(new RecordingsArchiveController(
            DefaultOptions, Recordings, new TranscriptTaskRepository(mongo.Database),
            new TranscriptModelRepository(mongo.Database), new TranscriptAutoEnqueueRepository(mongo.Database),
            AccountInfo, new FakeRecordingFileStatsClient(), NullLogger<RecordingsArchiveController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static int _seedSequence;

    private async Task SeedRecordingAsync(long roomId, long mid, string title)
    {
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = mid,
            Title = title,
            Files = ["[RAW] a.flv"],
            StartedAt = DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _seedSequence)),
            CreatedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task CandidateRooms_AggregatesDistinctRoomsWithLatestMid_AndAnchorName()
    {
        await SeedRecordingAsync(900001, 111, "第一场");
        await SeedRecordingAsync(900001, 222, "第二场"); // 同房间较新场次，Mid 应取最新
        await SeedRecordingAsync(900002, 333, "其他房间");
        await AccountInfo.UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = 222,
            RoomId = 900001,
            Data = """{"uName":"主播甲"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController().ListCandidateRooms());

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        // 按 roomId 升序
        Assert.Equal(900001, items[0].GetProperty("roomId").GetInt64());
        Assert.Equal(900002, items[1].GetProperty("roomId").GetInt64());
        Assert.Equal(222, items[0].GetProperty("mid").GetInt64());
        Assert.Equal("主播甲", items[0].GetProperty("anchorName").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("anchorName").ValueKind);
    }

    [Fact]
    public async Task CandidateRooms_NoRecordings_ReturnsEmpty()
    {
        var json = Ok(await CreateController().ListCandidateRooms());

        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }
}
