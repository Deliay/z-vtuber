using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.4：字幕下载端点——SRT/ASS golden 文本与 Content-Type/Content-Disposition（含 filename* 中文场景）、
/// format 400、taskId 非法/不存在 404、无片段合法空字幕、RUNNING 任务渲染已入库片段、文件名兜底。
/// </summary>
public class TranscriptSubtitleApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(fixture.Database);
    private TranscriptTaskRepository Tasks => new(fixture.Database);
    private TranscriptSegmentRepository Segments => new(fixture.Database);
    private TranscriptModelRepository Models => new(fixture.Database);

    private TranscriptSubtitleController CreateController() =>
        WithContext(new TranscriptSubtitleController(Recordings, Tasks, Segments, Models));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private async Task<RecordingRecord> SeedRecordingAsync(string title)
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
        string title = "深夜杂谈",
        string modelName = "qwen-fun-asr",
        string status = TranscriptTaskStatus.Done)
    {
        var recording = await SeedRecordingAsync(title);
        var model = await SeedModelAsync(modelName);
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

    private static JsonElement ErrorJson(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result).Value);

    private static JsonElement NotFoundJson(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<NotFoundObjectResult>(result).Value);

    private static async Task<byte[]> ReadBodyAsync(ControllerBase controller)
    {
        controller.HttpContext.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await controller.HttpContext.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 以 MVC 默认的 FileContentResult 执行器落地结果，使框架生成的 Content-Disposition（含 filename*）可在测试中断言。
    /// </summary>
    private static async Task ExecuteAsync(ControllerBase controller, IActionResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActionResultExecutor<FileContentResult>, FileContentResultExecutor>();
        services.AddSingleton<FileContentResultExecutor>();
        using var provider = services.BuildServiceProvider();
        controller.HttpContext.RequestServices = provider;
        controller.HttpContext.Request.Method = HttpMethods.Get;
        await result.ExecuteResultAsync(new ActionContext(
            controller.HttpContext,
            controller.RouteData ?? new RouteData(),
            new ActionDescriptor()));
    }

    [Fact]
    public async Task Subtitle_DefaultFormat_ReturnsGoldenSrtWithBomAndContentType()
    {
        var task = await SeedTaskAsync();
        // 乱序插入：断言按 StartTime 升序渲染（与 ListByTaskAsync 同源）
        await AddSegmentAsync(task.Id, 6000, 7000, "第二句");
        await AddSegmentAsync(task.Id, 1234, 5678, "第一句");

        var controller = CreateController();
        var result = await controller.DownloadSubtitle(task.Id.ToString(), format: null);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/x-subrip; charset=utf-8", file.ContentType);
        Assert.Equal("深夜杂谈-qwen-fun-asr.srt", file.FileDownloadName);

        // UTF-8 with BOM + golden 文本（升序）
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3).ToArray());
        const string golden =
            "1\n00:00:01,234 --> 00:00:05,678\n第一句\n\n" +
            "2\n00:00:06,000 --> 00:00:07,000\n第二句\n\n";
        Assert.Equal(golden, Encoding.UTF8.GetString(file.FileContents, 3, file.FileContents.Length - 3));

        await ExecuteAsync(controller, result);
        Assert.Equal("application/x-subrip; charset=utf-8", controller.Response.ContentType);

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("attachment", disposition);
        Assert.Contains("filename=", disposition);
        Assert.Contains("filename*=UTF-8''", disposition);
        // 中文场次标题经 filename* 百分号编码（深 → %E6%B7%B1）
        Assert.Contains("%E6%B7%B1", disposition);
    }

    [Fact]
    public async Task Subtitle_AssFormat_ReturnsGoldenAss()
    {
        var task = await SeedTaskAsync();
        await AddSegmentAsync(task.Id, 1234, 5678, "第一句");
        await AddSegmentAsync(task.Id, 60_567, 90_999, "含{括号}\n换行");

        var controller = CreateController();
        var result = await controller.DownloadSubtitle(task.Id.ToString(), format: "ass");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/x-ass; charset=utf-8", file.ContentType);
        Assert.Equal("深夜杂谈-qwen-fun-asr.ass", file.FileDownloadName);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3).ToArray());

        var text = Encoding.UTF8.GetString(file.FileContents, 3, file.FileContents.Length - 3);
        Assert.StartsWith(
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 2\nScaledBorderAndShadow: yes\n",
            text);
        Assert.Contains(
            "Style: Default,Microsoft YaHei,64,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,60,1\n",
            text);
        Assert.Contains("[Events]\n", text);
        // 厘秒向下取整 + 转义
        Assert.Contains("Dialogue: 0,0:00:01.23,0:00:05.67,Default,,0,0,0,,第一句\n", text);
        Assert.Contains("Dialogue: 0,0:01:00.56,0:01:30.99,Default,,0,0,0,,含\\{括号\\}\\N换行\n", text);

        await ExecuteAsync(controller, result);
        Assert.Equal("text/x-ass; charset=utf-8", controller.Response.ContentType);
        Assert.Contains("filename*=UTF-8''", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Subtitle_UnsupportedFormat_Returns400()
    {
        var task = await SeedTaskAsync();

        var result = await CreateController().DownloadSubtitle(task.Id.ToString(), format: "ssa");

        Assert.Equal(
            "unsupported format",
            ErrorJson(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Subtitle_InvalidTaskId_Returns404()
    {
        var controller = CreateController();

        var result = await controller.DownloadSubtitle("not-an-object-id", format: null);

        Assert.Equal("task not found", NotFoundJson(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Subtitle_UnknownTask_Returns404()
    {
        var controller = CreateController();

        var result = await controller.DownloadSubtitle(ObjectId.GenerateNewId().ToString(), format: "ass");

        Assert.Equal("task not found", NotFoundJson(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Subtitle_TaskWithoutSegments_Returns200EmptySrt()
    {
        var task = await SeedTaskAsync();

        var controller = CreateController();
        var result = await controller.DownloadSubtitle(task.Id.ToString(), format: null);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents);
    }

    [Fact]
    public async Task Subtitle_TaskWithoutSegments_Returns200AssHeaderOnly()
    {
        var task = await SeedTaskAsync();

        var result = await CreateController().DownloadSubtitle(task.Id.ToString(), format: "ass");

        var file = Assert.IsType<FileContentResult>(result);
        var text = Encoding.UTF8.GetString(file.FileContents, 3, file.FileContents.Length - 3);
        Assert.StartsWith("[Script Info]\n", text);
        Assert.Contains("[Events]\n", text);
        Assert.DoesNotContain("Dialogue:", text);
    }

    [Fact]
    public async Task Subtitle_RunningTask_RendersPersistedSegments()
    {
        var task = await SeedTaskAsync(status: TranscriptTaskStatus.Running);
        await AddSegmentAsync(task.Id, 1000, 2000, "已入库片段");

        var result = await CreateController().DownloadSubtitle(task.Id.ToString(), format: null);

        var file = Assert.IsType<FileContentResult>(result);
        var text = Encoding.UTF8.GetString(file.FileContents, 3, file.FileContents.Length - 3);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\n已入库片段\n\n", text);
    }

    [Fact]
    public async Task Subtitle_SpecialCharTitle_FallsBackToTranscriptFileName()
    {
        var task = await SeedTaskAsync(title: "!!!");

        var result = await CreateController().DownloadSubtitle(task.Id.ToString(), format: null);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal($"transcript-{task.Id.ToString()[..8]}.srt", file.FileDownloadName);
    }
}
