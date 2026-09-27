using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.6：`TrainingDatasetItemsController.Import` 控制器直测（stub `ITranscriptApiClient`）——
/// DONE 任务导入字段逐项映射、offset≠0 起止为偏移后而 AudioKey 用原始 start、重复导入与
/// 偏移变更后重导仍 `imported=0`、非 DONE 400 `task not done`、taskId 非法/不存在 404、
/// 数据集 404、上游异常 502 `transcript service unavailable`、`file` 为空照常导入。
/// </summary>
public class TrainingImportApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetItemsController CreateController(ITranscriptApiClient transcript)
    {
        var controller = new TrainingDatasetItemsController(
            Datasets, Items, transcript, NullLogger<TrainingDatasetItemsController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static T PayloadOf<T>(ActionResult<T> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        return Assert.IsType<T>(objectResult.Value);
    }

    private static string ErrorOf<T>(ActionResult<T> result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private async Task<ObjectId> SeedDatasetAsync(string name = "音频数据集")
    {
        var now = DateTime.UtcNow;
        var record = await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = name,
            Type = "audio",
            CreatedAt = now,
            UpdatedAt = now,
        });
        return record.Id;
    }

    /// <summary>构造任务详情：片段的 start/end 为详情返回口径（已叠加任务偏移）。</summary>
    private static TranscriptTaskDetail Detail(
        string taskId,
        TranscriptSegmentBrief[] segments,
        string status = TranscriptTaskStatus.Done,
        long roomId = 700001,
        string? file = "a.flv",
        long offsetMs = 0) =>
        new(new TranscriptTaskBrief(taskId, roomId, file, status, offsetMs), segments);

    private static string NewTaskId() => ObjectId.GenerateNewId().ToString();

    // ---- 导入（接口 6）----

    [Fact]
    public async Task Import_DoneTask_MapsItemFields()
    {
        var datasetId = await SeedDatasetAsync();
        var taskId = NewTaskId();
        var stub = new StubTranscriptClient
        {
            Detail = Detail(taskId,
            [
                new TranscriptSegmentBrief(1000, 3500, "你好"),
                new TranscriptSegmentBrief(4000, 7000, "世界"),
            ]),
        };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(taskId));

        var dto = PayloadOf(result);
        Assert.Equal(2, dto.Imported);
        Assert.Equal(0, dto.Skipped);

        var stored = await Items.ListAsync(datasetId);
        Assert.Equal(2, stored.Count);
        var first = stored[0];
        Assert.Equal("transcript", first.AudioType);
        Assert.Equal(taskId, first.Source);
        Assert.Equal("b/live/700001/recordings/a.flv", first.AudioUri);
        Assert.Equal($"transcript:{taskId}:1000", first.AudioKey);
        Assert.Equal(1000, first.StartMs);
        Assert.Equal(3500, first.EndMs);
        Assert.Equal("你好", first.Text);
        Assert.Equal(datasetId, first.DatasetId);
        Assert.True(first.CreatedAt != default);
        Assert.Equal($"transcript:{taskId}:4000", stored[1].AudioKey);
        Assert.Equal(7000, stored[1].EndMs);
        Assert.Equal("世界", stored[1].Text);
    }

    [Fact]
    public async Task Import_OffsetTask_UsesShiftedTimesAndRawStartAudioKey()
    {
        var datasetId = await SeedDatasetAsync();
        var taskId = NewTaskId();
        // 详情返回值已叠加任务偏移 2500ms：AudioKey 必须用原始 start（返回 start − offsetMs）
        var stub = new StubTranscriptClient
        {
            Detail = Detail(taskId, [new TranscriptSegmentBrief(3500, 6000, "偏移句")], offsetMs: 2500),
        };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(taskId));

        Assert.Equal(1, PayloadOf(result).Imported);
        var item = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal(3500, item.StartMs);
        Assert.Equal(6000, item.EndMs);
        Assert.Equal($"transcript:{taskId}:1000", item.AudioKey);
        Assert.Equal("偏移句", item.Text);
    }

    [Fact]
    public async Task Import_Reimport_SkipsAllDuplicates()
    {
        var datasetId = await SeedDatasetAsync();
        var taskId = NewTaskId();
        var stub = new StubTranscriptClient
        {
            Detail = Detail(taskId,
            [
                new TranscriptSegmentBrief(0, 2000, "一句"),
                new TranscriptSegmentBrief(2000, 4000, "二句"),
                new TranscriptSegmentBrief(4000, 6000, "三句"),
            ]),
        };
        var controller = CreateController(stub);
        var request = new TrainingDatasetImportRequest(taskId);

        Assert.Equal(3, PayloadOf(await controller.Import(datasetId.ToString(), request)).Imported);
        var second = PayloadOf(await controller.Import(datasetId.ToString(), request));

        Assert.Equal(0, second.Imported);
        Assert.Equal(3, second.Skipped);
        Assert.Equal(3, (await Items.ListAsync(datasetId)).Count);
    }

    [Fact]
    public async Task Import_AfterOffsetChange_ReimportStillSkipsAll()
    {
        var datasetId = await SeedDatasetAsync();
        var taskId = NewTaskId();
        var stub = new StubTranscriptClient
        {
            Detail = Detail(taskId,
            [
                new TranscriptSegmentBrief(1000, 2000, "一句"),
                new TranscriptSegmentBrief(3000, 4000, "二句"),
            ]),
        };
        var controller = CreateController(stub);
        var request = new TrainingDatasetImportRequest(taskId);
        Assert.Equal(2, PayloadOf(await controller.Import(datasetId.ToString(), request)).Imported);

        // 任务偏移变更（详情返回值整体平移 5000ms）：AudioKey 仍按原始 start 生成 → 去重不失效
        stub.Detail = Detail(taskId,
        [
            new TranscriptSegmentBrief(6000, 7000, "一句"),
            new TranscriptSegmentBrief(8000, 9000, "二句"),
        ], offsetMs: 5000);

        var second = PayloadOf(await controller.Import(datasetId.ToString(), request));

        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);
        var stored = await Items.ListAsync(datasetId);
        Assert.Equal(2, stored.Count);
        // 已导入条目不被覆盖
        Assert.Equal(new long[] { 1000, 3000 }, stored.Select(i => i.StartMs).ToArray());
    }

    [Theory]
    [InlineData(TranscriptTaskStatus.Pending)]
    [InlineData(TranscriptTaskStatus.Running)]
    [InlineData(TranscriptTaskStatus.Failed)]
    public async Task Import_NotDoneTask_Returns400TaskNotDone(string status)
    {
        var datasetId = await SeedDatasetAsync();
        var taskId = NewTaskId();
        var stub = new StubTranscriptClient
        {
            Detail = Detail(taskId, [new TranscriptSegmentBrief(0, 1000, "句")], status: status),
        };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(taskId));

        Assert.Equal("task not done", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Items.ListAsync(datasetId));
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("")]
    public async Task Import_InvalidTaskId_Returns404TaskNotFound(string taskId)
    {
        var datasetId = await SeedDatasetAsync();
        var stub = new StubTranscriptClient { Detail = Detail(NewTaskId(), []) };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(taskId));

        Assert.Equal("task not found", ErrorOf(result, StatusCodes.Status404NotFound));
        // 非法 taskId 不请求 transcript-api
        Assert.Equal(0, stub.Calls);
        Assert.Empty(await Items.ListAsync(datasetId));
    }

    [Fact]
    public async Task Import_UnknownTask_Returns404TaskNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        var stub = new StubTranscriptClient { Detail = null };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(NewTaskId()));

        Assert.Equal("task not found", ErrorOf(result, StatusCodes.Status404NotFound));
        Assert.Empty(await Items.ListAsync(datasetId));
    }

    [Fact]
    public async Task Import_MissingTaskId_Returns404TaskNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        var stub = new StubTranscriptClient { Detail = Detail(NewTaskId(), []) };

        var missing = await CreateController(stub).Import(datasetId.ToString(), null);
        Assert.Equal("task not found", ErrorOf(missing, StatusCodes.Status404NotFound));

        var blank = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest("   "));
        Assert.Equal("task not found", ErrorOf(blank, StatusCodes.Status404NotFound));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task Import_UnknownDataset_Returns404DatasetNotFound_AndSkipsTranscriptCall()
    {
        var taskId = NewTaskId();
        var stub = new StubTranscriptClient { Detail = Detail(taskId, []) };

        var result = await CreateController(stub)
            .Import(ObjectId.GenerateNewId().ToString(), new TrainingDatasetImportRequest(taskId));

        Assert.Equal("dataset not found", ErrorOf(result, StatusCodes.Status404NotFound));
        Assert.Equal(0, stub.Calls);

        var invalid = await CreateController(stub)
            .Import("not-an-objectid", new TrainingDatasetImportRequest(taskId));
        Assert.Equal("dataset not found", ErrorOf(invalid, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Import_HttpRequestException_Returns502TranscriptServiceUnavailable()
    {
        var datasetId = await SeedDatasetAsync();
        var stub = new StubTranscriptClient { Throw = new HttpRequestException("transcript down") };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(NewTaskId()));

        Assert.Equal("transcript service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
        Assert.Empty(await Items.ListAsync(datasetId));
    }

    [Fact]
    public async Task Import_TaskCanceledException_Returns502TranscriptServiceUnavailable()
    {
        var datasetId = await SeedDatasetAsync();
        var stub = new StubTranscriptClient { Throw = new TaskCanceledException("timeout") };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(NewTaskId()));

        Assert.Equal("transcript service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
        Assert.Empty(await Items.ListAsync(datasetId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Import_EmptyFile_ImportsWithEmptyAudioUri(string? file)
    {
        var datasetId = await SeedDatasetAsync();
        var taskId = NewTaskId();
        var stub = new StubTranscriptClient
        {
            Detail = Detail(taskId, [new TranscriptSegmentBrief(500, 1500, "无音频句")], file: file),
        };

        var result = await CreateController(stub)
            .Import(datasetId.ToString(), new TrainingDatasetImportRequest(taskId));

        Assert.Equal(1, PayloadOf(result).Imported);
        var item = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal(string.Empty, item.AudioUri);
        Assert.Equal($"transcript:{taskId}:500", item.AudioKey);
        Assert.Equal(500, item.StartMs);
        Assert.Equal(1500, item.EndMs);
        Assert.Equal("无音频句", item.Text);
    }

    /// <summary>可配置的 transcript-api 客户端桩。</summary>
    private sealed class StubTranscriptClient : ITranscriptApiClient
    {
        public TranscriptTaskDetail? Detail { get; set; }

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public Task<TranscriptTaskDetail?> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        {
            Calls++;
            return Throw is null
                ? Task.FromResult(Detail)
                : Task.FromException<TranscriptTaskDetail?>(Throw);
        }
    }
}
