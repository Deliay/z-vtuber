using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Clients;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Controllers;

namespace Z.Vtuber.Tests;

public class RecordingsArchiveFilterTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly FakeRecordingFileStatsClient _fileStats = new();

    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);
    private TranscriptTaskRepository Tasks => new(mongo.Database);
    private TranscriptModelRepository Models => new(mongo.Database);
    private TranscriptAutoEnqueueRepository AutoEnqueue => new(mongo.Database);
    private AccountBasicInfoRepository AccountInfo => new(mongo.Database);

    private static readonly IOptions<TranscriptApiOptions> DefaultOptions =
        Options.Create(new TranscriptApiOptions());

    private RecordingsArchiveController CreateController(IRecordingFileStatsClient? fileStats = null) =>
        WithContext(new RecordingsArchiveController(
            DefaultOptions, Recordings, Tasks, Models, AutoEnqueue,
            AccountInfo, fileStats ?? _fileStats, NullLogger<RecordingsArchiveController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private async Task<RecordingRecord> SeedRecordingAsync(
        long roomId, long mid, string title, params string[] files)
    {
        var record = new RecordingRecord
        {
            RoomId = roomId,
            Mid = mid,
            Title = title,
            Files = [.. files],
            StartedAt = DateTime.UtcNow.AddSeconds(-Interlocked.Increment(ref _seedSequence)),
            CreatedAt = DateTime.UtcNow,
        };
        await Recordings.InsertAsync(record);
        return record;
    }

    private static int _seedSequence;

    [Fact]
    public async Task ListRecordings_FiltersRecordingsWithNoExistingFiles()
    {
        await SeedRecordingAsync(800001, 111, "有文件", "[RAW] a.flv");
        await SeedRecordingAsync(800002, 222, "空录播", "[RAW] b.flv");
        _fileStats.Missing.Add((800002, "[RAW] b.flv"));

        var json = Ok(await CreateController().ListRecordings());

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal(800001, item.GetProperty("roomId").GetInt64());
    }

    [Fact]
    public async Task ListRecordings_ShrinksFilesToExistingSubset_AndSumsSize()
    {
        await SeedRecordingAsync(800010, 333, "部分存在", "[RAW] a.flv", "[AV1] a.flv");
        _fileStats.Missing.Add((800010, "[AV1] a.flv"));
        _fileStats.Sizes[(800010, "[RAW] a.flv")] = 12345;

        var json = Ok(await CreateController().ListRecordings());

        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        var files = item.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Equal(["[RAW] a.flv"], files);
        Assert.Equal(12345, item.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task ListRecordings_JoinsAnchorName_AndNullWhenMissing()
    {
        await SeedRecordingAsync(800020, 444, "有主播", "[RAW] a.flv");
        await SeedRecordingAsync(800021, 555, "无主播", "[RAW] b.flv");
        await AccountInfo.UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = 444,
            RoomId = 800020,
            Data = """{"uName":"某主播"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController().ListRecordings());
        var items = json.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal("某主播", items.Single(i => i.GetProperty("roomId").GetInt64() == 800020)
            .GetProperty("anchorName").GetString());
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("roomId").GetInt64() == 800021)
            .GetProperty("anchorName").ValueKind);
    }

    [Fact]
    public async Task ListRecordings_RecordingUnavailable_DegradesToUnfiltered()
    {
        await SeedRecordingAsync(800030, 666, "场次一", "[RAW] a.flv");
        await SeedRecordingAsync(800031, 666, "场次二", "[RAW] b.flv");
        _fileStats.Throw = true;

        var json = Ok(await CreateController().ListRecordings());

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("size").ValueKind));
        Assert.All(items, item => Assert.NotEmpty(item.GetProperty("files").EnumerateArray()));
    }

    [Fact]
    public async Task GetRoomTranscripts_FiltersEmptyRecordings()
    {
        var kept = await SeedRecordingAsync(800040, 777, "有文件", "[RAW] a.flv");
        await SeedRecordingAsync(800040, 777, "空录播", "[RAW] b.flv");
        _fileStats.Missing.Add((800040, "[RAW] b.flv"));

        var json = Ok(await CreateController().GetRoomTranscripts(800040));

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal(kept.Id.ToString(), item.GetProperty("recordingId").GetString());
    }

    [Fact]
    public async Task ListRecordings_LimitBeyondSingleFileStatsRequest_StillFiltersEmptyRecordings()
    {
        // limit=120 > recording file-stats 单请求上限 100：客户端需分块，过滤仍须生效
        var handler = new EchoRecordingFileStatsHandler();
        handler.Missing.Add((950000, "[RAW] a.flv"));
        var realClient = new RecordingFileStatsClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://recording/") },
            NullLogger<RecordingFileStatsClient>.Instance);

        for (var i = 0; i < 120; i++)
        {
            await SeedRecordingAsync(950000 + i, 111, $"场次{i}", "[RAW] a.flv");
        }

        var json = Ok(await CreateController(realClient).ListRecordings(limit: 120));

        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(119, items.Length);
        Assert.DoesNotContain(items, i => i.GetProperty("roomId").GetInt64() == 950000);
        Assert.Equal([100, 20], handler.BatchItemCounts);
    }
}
