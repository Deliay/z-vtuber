using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Controllers;
using Z.Vtuber.RecordingTranscript.Api.Sse;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.2：PUT /api/transcripts/task/{taskId}/offset——正/负/零值 200、±24h 越界与缺失 400、
/// taskId 非法或不存在 404、RUNNING 任务可设置；任务详情与 SRT/ASS 字幕导出统一应用任务播放偏移
/// （映射层不钳制负值、字幕渲染钳 0）；存量无 OffsetMs 字段的任务行为与现状一致。
/// </summary>
[Collection("redis")]
public class TranscriptTaskOffsetApiTests(MongoDbFixture mongo, RedisFixture redis)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private const long MaxOffsetMs = 86_400_000;

    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);
    private TranscriptTaskRepository Tasks => new(mongo.Database);
    private TranscriptSegmentRepository Segments => new(mongo.Database);
    private TranscriptModelRepository Models => new(mongo.Database);

    private static readonly IOptions<TranscriptApiOptions> DefaultOptions =
        Options.Create(new TranscriptApiOptions());

    private TranscriptTasksController CreateTasksController() =>
        WithContext(new TranscriptTasksController(
            DefaultOptions,
            Recordings,
            Tasks,
            Segments,
            Models,
            new TranscriptSegmentStream(
                Segments, Tasks, redis.Connection, NullLogger<TranscriptSegmentStream>.Instance)));

    private TranscriptSubtitleController CreateSubtitleController() =>
        WithContext(new TranscriptSubtitleController(Recordings, Tasks, Segments, Models));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static JsonElement ErrorJson(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result).Value);

    private static JsonElement NotFoundJson(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<NotFoundObjectResult>(result).Value);

    private async Task<RecordingRecord> SeedRecordingAsync(string title = "深夜杂谈")
    {
        var record = new RecordingRecord
        {
            RoomId = 700001,
            Mid = 111,
            Title = title,
            Files = ["[RAW] a.flv"],
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        await Recordings.InsertAsync(record);
        return record;
    }

    private async Task<TranscriptModelRecord> SeedModelAsync(string name = "qwen-fun-asr")
    {
        var model = new TranscriptModelRecord
        {
            Name = name,
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "super-secret",
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = 1,
            CreatedAt = DateTime.UtcNow,
        };
        await Models.InsertAsync(model);
        return model;
    }

    private async Task<TranscriptTaskRecord> SeedTaskAsync(
        string status = TranscriptTaskStatus.Done, long? offsetMs = null)
    {
        var recording = await SeedRecordingAsync();
        var model = await SeedModelAsync();
        var now = DateTime.UtcNow;
        var task = new TranscriptTaskRecord
        {
            RecordingId = recording.Id,
            RoomId = recording.RoomId,
            File = "[RAW] a.flv",
            ModelId = model.Id,
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Manual,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (offsetMs.HasValue)
        {
            task.OffsetMs = offsetMs.Value;
        }

        await Tasks.InsertAsync(task);
        return task;
    }

    private Task AddSegmentAsync(ObjectId taskId, long start, long end, string text) =>
        Segments.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = taskId,
            StartTime = start,
            EndTime = end,
            Text = text,
        });

    private async Task<ObjectId> SeedLegacyTaskWithoutOffsetAsync()
    {
        // 存量文档缺 OffsetMs 字段（无迁移）：反序列化为 0，行为与现状一致
        var recording = await SeedRecordingAsync();
        var model = await SeedModelAsync();
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        var collection = mongo.Database.GetCollection<BsonDocument>(MongoCollectionNames.TranscriptTasks);
        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "RecordingId", recording.Id },
            { "RoomId", recording.RoomId },
            { "File", "[RAW] a.flv" },
            { "ModelId", model.Id },
            { "Type", TranscriptTaskTypes.Recording },
            { "Mode", TranscriptTaskModes.File },
            { "Source", TranscriptTaskSources.Manual },
            { "Position", 0L },
            { "Status", TranscriptTaskStatus.Done },
            { "CreatedAt", now },
            { "UpdatedAt", now },
        });
        return id;
    }

    private static string ReadSubtitle(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        return Encoding.UTF8.GetString(file.FileContents, 3, file.FileContents.Length - 3);
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(-1500)]
    [InlineData(0)]
    public async Task UpdateOffset_ValidValue_Returns200WithOffsetMs(long offsetMs)
    {
        var task = await SeedTaskAsync();

        var json = Ok(await CreateTasksController().UpdateTaskOffset(
            task.Id.ToString(), new UpdateTranscriptTaskOffsetRequest(offsetMs)));

        Assert.Equal(offsetMs, json.GetProperty("offsetMs").GetInt64());
        Assert.Equal(offsetMs, (await Tasks.GetByIdAsync(task.Id))!.OffsetMs);
    }

    [Theory]
    [InlineData(MaxOffsetMs + 1)]
    [InlineData(-MaxOffsetMs - 1)]
    public async Task UpdateOffset_BeyondTwentyFourHours_Returns400OutOfRange(long offsetMs)
    {
        var task = await SeedTaskAsync();

        var result = await CreateTasksController().UpdateTaskOffset(
            task.Id.ToString(), new UpdateTranscriptTaskOffsetRequest(offsetMs));

        Assert.Equal(
            "offsetMs out of range",
            ErrorJson(result).GetProperty("error").GetString());
        Assert.Equal(0, (await Tasks.GetByIdAsync(task.Id))!.OffsetMs);
    }

    [Theory]
    [InlineData(MaxOffsetMs)]
    [InlineData(-MaxOffsetMs)]
    public async Task UpdateOffset_BoundaryValues_AreAccepted(long offsetMs)
    {
        var task = await SeedTaskAsync();

        var json = Ok(await CreateTasksController().UpdateTaskOffset(
            task.Id.ToString(), new UpdateTranscriptTaskOffsetRequest(offsetMs)));

        Assert.Equal(offsetMs, json.GetProperty("offsetMs").GetInt64());
    }

    [Fact]
    public async Task UpdateOffset_MissingOffsetMs_Returns400OutOfRange()
    {
        var task = await SeedTaskAsync();

        var result = await CreateTasksController().UpdateTaskOffset(
            task.Id.ToString(), new UpdateTranscriptTaskOffsetRequest(null));

        Assert.Equal(
            "offsetMs out of range",
            ErrorJson(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateOffset_InvalidTaskId_Returns404()
    {
        var result = await CreateTasksController().UpdateTaskOffset(
            "not-an-object-id", new UpdateTranscriptTaskOffsetRequest(2500));

        Assert.Equal("task not found", NotFoundJson(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateOffset_UnknownTask_Returns404()
    {
        var result = await CreateTasksController().UpdateTaskOffset(
            ObjectId.GenerateNewId().ToString(), new UpdateTranscriptTaskOffsetRequest(2500));

        Assert.Equal("task not found", NotFoundJson(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateOffset_RunningTask_IsAllowed()
    {
        var task = await SeedTaskAsync(status: TranscriptTaskStatus.Running);

        var json = Ok(await CreateTasksController().UpdateTaskOffset(
            task.Id.ToString(), new UpdateTranscriptTaskOffsetRequest(2500)));

        Assert.Equal(TranscriptTaskStatus.Running, json.GetProperty("status").GetString());
        Assert.Equal(2500, json.GetProperty("offsetMs").GetInt64());
        Assert.Equal(2500, (await Tasks.GetByIdAsync(task.Id))!.OffsetMs);
    }

    [Fact]
    public async Task GetTask_WithOffset_ShiftsSegmentTimesAndReportsOffsetMs()
    {
        var task = await SeedTaskAsync(offsetMs: 2500);
        await AddSegmentAsync(task.Id, 1000, 1500, "第一句");

        var json = Ok(await CreateTasksController().GetTask(task.Id.ToString()));

        Assert.Equal(2500, json.GetProperty("task").GetProperty("offsetMs").GetInt64());
        var segment = Assert.Single(json.GetProperty("segments").EnumerateArray());
        Assert.Equal(3500, segment.GetProperty("start").GetInt64());
        Assert.Equal(4000, segment.GetProperty("end").GetInt64());
        Assert.Equal("第一句", segment.GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetTask_NegativeOffset_DoesNotClampSegmentTimes()
    {
        var task = await SeedTaskAsync(offsetMs: -2000);
        await AddSegmentAsync(task.Id, 1000, 1500, "前移句");

        var json = Ok(await CreateTasksController().GetTask(task.Id.ToString()));

        var segment = Assert.Single(json.GetProperty("segments").EnumerateArray());
        Assert.Equal(-1000, segment.GetProperty("start").GetInt64());
        Assert.Equal(-500, segment.GetProperty("end").GetInt64());
    }

    [Fact]
    public async Task GetTask_LegacyTaskWithoutOffsetField_KeepsOriginalTimes()
    {
        var taskId = await SeedLegacyTaskWithoutOffsetAsync();
        await AddSegmentAsync(taskId, 1000, 1500, "存量句");

        var json = Ok(await CreateTasksController().GetTask(taskId.ToString()));

        Assert.Equal(0, json.GetProperty("task").GetProperty("offsetMs").GetInt64());
        var segment = Assert.Single(json.GetProperty("segments").EnumerateArray());
        Assert.Equal(1000, segment.GetProperty("start").GetInt64());
        Assert.Equal(1500, segment.GetProperty("end").GetInt64());
    }

    [Fact]
    public async Task Subtitle_SrtWithOffset_ShiftsEntryTimes()
    {
        var task = await SeedTaskAsync(offsetMs: 2500);
        // 乱序插入：按 StartTime 升序渲染
        await AddSegmentAsync(task.Id, 6000, 7000, "第二句");
        await AddSegmentAsync(task.Id, 1234, 5678, "第一句");

        var controller = CreateSubtitleController();
        var result = await controller.DownloadSubtitle(task.Id.ToString(), format: null);

        Assert.Equal(
            "1\n00:00:03,734 --> 00:00:08,178\n第一句\n\n" +
            "2\n00:00:08,500 --> 00:00:09,500\n第二句\n\n",
            ReadSubtitle(result));
    }

    [Fact]
    public async Task Subtitle_AssWithOffset_ShiftsDialogueTimes()
    {
        var task = await SeedTaskAsync(offsetMs: 2500);
        await AddSegmentAsync(task.Id, 1234, 5678, "第一句");

        var controller = CreateSubtitleController();
        var result = await controller.DownloadSubtitle(task.Id.ToString(), format: "ass");

        Assert.Contains(
            "Dialogue: 0,0:00:03.73,0:00:08.17,Default,,0,0,0,,第一句\n",
            ReadSubtitle(result));
    }

    [Fact]
    public async Task Subtitle_NegativeOffset_ClampsEntryTimesToZero()
    {
        var task = await SeedTaskAsync(offsetMs: -2000);
        await AddSegmentAsync(task.Id, 1000, 1500, "越界句");
        await AddSegmentAsync(task.Id, 1234, 5678, "第一句");

        var controller = CreateSubtitleController();
        var result = await controller.DownloadSubtitle(task.Id.ToString(), format: null);

        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:00,000\n越界句\n\n" +
            "2\n00:00:00,000 --> 00:00:03,678\n第一句\n\n",
            ReadSubtitle(result));
    }
}
