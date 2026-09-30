using System.Text.Json;
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
/// Plan 1.5：`TrainingDatasetItemsController`（列表/更新/批量删除）控制器直测（Testcontainers Mongo）——
/// 列表七字段 + `audioType`/`source` 过滤与排序；更新四字段单独与组合（缺失字段不变）、
/// `endMs` 显式 null 置空、非法时间 400 `invalid time range`、显式 null 400 `field cannot be null`、404；
/// 批量删除部分 id 不存在仍删其余并返回 `deleted`、数据集 404。
/// </summary>
public class TrainingDatasetItemsApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetItemsController CreateController() =>
        WithContext(new TrainingDatasetItemsController(
            Datasets, Items, new StubTranscriptClient(), NullLogger<TrainingDatasetItemsController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
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

    /// <summary>从原始 JSON 构造更新请求：缺失字段保持 <c>JsonValueKind.Undefined</c>。</summary>
    private static TrainingDatasetItemUpdateRequest UpdateFrom(string json) =>
        JsonSerializer.Deserialize<TrainingDatasetItemUpdateRequest>(json)!;

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

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(
        ObjectId datasetId,
        string audioKey,
        string audioType = "transcript",
        string source = "task-1",
        string audioUri = "b/live/700001/recordings/a.flv",
        long startMs = 1000,
        long? endMs = 5000,
        string text = "原句",
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var record = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = audioType,
            Source = source,
            AudioUri = audioUri,
            AudioKey = audioKey,
            StartMs = startMs,
            EndMs = endMs,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([record]);
        return record;
    }

    // ---- 列表（接口 5）----

    [Fact]
    public async Task List_ReturnsSevenFields_OrdersByCreatedAtThenAudioKey()
    {
        var datasetId = await SeedDatasetAsync();
        var baseTime = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        var newer = await SeedItemAsync(datasetId, "transcript:task-1:9000",
            startMs: 9000, endMs: null, text: "末句", createdAt: baseTime.AddMinutes(1));
        var second = await SeedItemAsync(datasetId, "transcript:task-1:2000",
            audioType: "recording", source: "rec-88", audioUri: "b/live/100/recordings/b.flv",
            startMs: 2000, endMs: 4000, text: "第二句", createdAt: baseTime);
        var first = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 1000, endMs: 3000, text: "第一句", createdAt: baseTime);

        var list = PayloadOf(await CreateController().List(datasetId.ToString(), null, null));

        Assert.Equal(
            new[] { first.Id, second.Id, newer.Id },
            list.Items.Select(i => ObjectId.Parse(i.Id)).ToArray());

        // 七字段逐项断言
        var dto = list.Items[0];
        Assert.Equal("transcript", dto.AudioType);
        Assert.Equal("task-1", dto.Source);
        Assert.Equal("b/live/700001/recordings/a.flv", dto.AudioUri);
        Assert.Equal("transcript:task-1:1000", dto.AudioKey);
        Assert.Equal(1000, dto.StartMs);
        Assert.Equal(3000, dto.EndMs);
        Assert.Equal("第一句", dto.Text);

        Assert.Equal(datasetId.ToString(), dto.DatasetId);
        Assert.Equal("recording", list.Items[1].AudioType);
        Assert.Equal("rec-88", list.Items[1].Source);
        Assert.Null(list.Items[2].EndMs);
        Assert.Null(dto.Extensions);
    }

    [Fact]
    public async Task List_FiltersByAudioTypeAndSource()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "transcript:task-1:0", source: "task-1");
        await SeedItemAsync(datasetId, "transcript:task-1:1000", source: "task-1");
        await SeedItemAsync(datasetId, "transcript:task-2:0", source: "task-2");
        await SeedItemAsync(datasetId, "recording:88:0", audioType: "recording", source: "88");
        var otherDatasetId = await SeedDatasetAsync("另一个数据集");
        await SeedItemAsync(otherDatasetId, "transcript:task-1:0", source: "task-1");
        var controller = CreateController();

        var byType = PayloadOf(await controller.List(datasetId.ToString(), "transcript", null));
        Assert.Equal(3, byType.Items.Count);
        Assert.All(byType.Items, i => Assert.Equal("transcript", i.AudioType));

        var bySource = PayloadOf(await controller.List(datasetId.ToString(), null, "task-2"));
        var single = Assert.Single(bySource.Items);
        Assert.Equal("transcript:task-2:0", single.AudioKey);

        var byBoth = PayloadOf(await controller.List(datasetId.ToString(), "recording", "88"));
        Assert.Equal("recording:88:0", Assert.Single(byBoth.Items).AudioKey);

        Assert.Empty(PayloadOf(await controller.List(datasetId.ToString(), "missing", null)).Items);
    }

    [Fact]
    public async Task List_UnknownOrInvalidDataset_Returns404()
    {
        var controller = CreateController();

        var unknown = await controller.List(ObjectId.GenerateNewId().ToString(), null, null);
        Assert.Equal("dataset not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var invalid = await controller.List("not-an-objectid", null, null);
        Assert.Equal("dataset not found", ErrorOf(invalid, StatusCodes.Status404NotFound));
    }

    // ---- 更新（接口 7）----

    [Fact]
    public async Task Update_AudioUriOnly_UpdatesAudioUriKeepsOthers()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000");

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(),
            UpdateFrom("""{"audioUri":"b/live/100/recordings/new.flv"}"""));

        var dto = PayloadOf(result);
        Assert.Equal("b/live/100/recordings/new.flv", dto.AudioUri);
        Assert.Equal(item.Id.ToString(), dto.Id);

        var stored = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal("b/live/100/recordings/new.flv", stored.AudioUri);
        Assert.Equal(1000, stored.StartMs);
        Assert.Equal(5000, stored.EndMs);
        Assert.Equal("原句", stored.Text);
        Assert.Equal("transcript:task-1:1000", stored.AudioKey);
    }

    [Fact]
    public async Task Update_TextOnly_UpdatesTextAndRefreshesUpdatedAt()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000");
        var before = (await Items.ListAsync(datasetId)).Single().UpdatedAt;
        await TestWait.UntilAsync(() => DateTime.UtcNow > before, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(2));

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(), UpdateFrom("""{"text":"改写后的句子"}"""));

        var dto = PayloadOf(result);
        Assert.Equal("改写后的句子", dto.Text);

        var stored = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal("改写后的句子", stored.Text);
        Assert.Equal("b/live/700001/recordings/a.flv", stored.AudioUri);
        Assert.Equal(1000, stored.StartMs);
        // 更新必须刷新 UpdatedAt（仓储不隐式刷新）
        Assert.True(stored.UpdatedAt > before);
        Assert.Equal(stored.UpdatedAt, dto.UpdatedAt);
    }

    [Fact]
    public async Task Update_StartMsOnly_UpdatesStartKeepsEnd()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 1000, endMs: 5000);

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(), UpdateFrom("""{"startMs":2000}"""));

        var dto = PayloadOf(result);
        Assert.Equal(2000, dto.StartMs);
        Assert.Equal(5000, dto.EndMs);

        var stored = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal(2000, stored.StartMs);
        Assert.Equal(5000, stored.EndMs);
        Assert.Equal("transcript:task-1:1000", stored.AudioKey);
    }

    [Fact]
    public async Task Update_EndMsOnly_UpdatesEndKeepsStart()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 1000, endMs: 5000);

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(), UpdateFrom("""{"endMs":9000}"""));

        var dto = PayloadOf(result);
        Assert.Equal(1000, dto.StartMs);
        Assert.Equal(9000, dto.EndMs);
        Assert.Equal(9000, (await Items.ListAsync(datasetId)).Single().EndMs);
    }

    [Fact]
    public async Task Update_CombinedFields_KeepsMissingFieldsUnchanged()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 1000, endMs: 5000, text: "原句");

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(),
            UpdateFrom("""{"text":"新句","endMs":8000}"""));

        var dto = PayloadOf(result);
        Assert.Equal("新句", dto.Text);
        Assert.Equal(8000, dto.EndMs);
        // 未提交的 audioUri / startMs 保持不变
        Assert.Equal("b/live/700001/recordings/a.flv", dto.AudioUri);
        Assert.Equal(1000, dto.StartMs);

        var stored = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal("新句", stored.Text);
        Assert.Equal(8000, stored.EndMs);
        Assert.Equal("b/live/700001/recordings/a.flv", stored.AudioUri);
        Assert.Equal(1000, stored.StartMs);
    }

    [Fact]
    public async Task Update_EndMsExplicitNull_ClearsEndMs()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 1000, endMs: 5000);

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(), UpdateFrom("""{"endMs":null}"""));

        var dto = PayloadOf(result);
        Assert.Null(dto.EndMs);
        Assert.Equal(1000, dto.StartMs);

        var stored = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Null(stored.EndMs);
        Assert.Equal(1000, stored.StartMs);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(4000)]
    public async Task Update_EndMsNotGreaterThanStart_Returns400InvalidTimeRange(long endMs)
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 5000, endMs: 9000);

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(),
            UpdateFrom($$"""{"startMs":5000,"endMs":{{endMs}}}"""));

        Assert.Equal("invalid time range", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Equal(5000, (await Items.ListAsync(datasetId)).Single().StartMs);
        Assert.Equal(9000, (await Items.ListAsync(datasetId)).Single().EndMs);
    }

    [Fact]
    public async Task Update_NegativeStartMs_Returns400InvalidTimeRange()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000",
            startMs: 1000, endMs: 5000);

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(), UpdateFrom("""{"startMs":-1}"""));

        Assert.Equal("invalid time range", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Equal(1000, (await Items.ListAsync(datasetId)).Single().StartMs);
    }

    [Theory]
    [InlineData("audioUri")]
    [InlineData("text")]
    [InlineData("startMs")]
    public async Task Update_ExplicitNullField_Returns400FieldCannotBeNull(string field)
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000");

        var result = await CreateController().Update(
            datasetId.ToString(), item.Id.ToString(),
            UpdateFrom($$"""{ "{{field}}":null }"""));

        Assert.Equal("field cannot be null", ErrorOf(result, StatusCodes.Status400BadRequest));
        var stored = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal("b/live/700001/recordings/a.flv", stored.AudioUri);
        Assert.Equal("原句", stored.Text);
        Assert.Equal(1000, stored.StartMs);
    }

    [Fact]
    public async Task Update_UnknownDataset_Returns404DatasetNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "transcript:task-1:1000");

        var result = await CreateController().Update(
            ObjectId.GenerateNewId().ToString(), item.Id.ToString(),
            UpdateFrom("""{"text":"新句"}"""));

        Assert.Equal("dataset not found", ErrorOf(result, StatusCodes.Status404NotFound));
        Assert.Equal("原句", (await Items.ListAsync(datasetId)).Single().Text);
    }

    [Fact]
    public async Task Update_UnknownOrInvalidItem_Returns404ItemNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "transcript:task-1:1000");
        var controller = CreateController();

        var unknown = await controller.Update(
            datasetId.ToString(), ObjectId.GenerateNewId().ToString(),
            UpdateFrom("""{"text":"新句"}"""));
        Assert.Equal("item not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var invalid = await controller.Update(
            datasetId.ToString(), "not-an-objectid", UpdateFrom("""{"text":"新句"}"""));
        Assert.Equal("item not found", ErrorOf(invalid, StatusCodes.Status404NotFound));

        Assert.Equal("原句", (await Items.ListAsync(datasetId)).Single().Text);
    }

    // ---- 批量删除（接口 8）----

    [Fact]
    public async Task BatchDelete_PartialUnknownIds_DeletesRestAndReturnsCount()
    {
        var datasetId = await SeedDatasetAsync();
        var first = await SeedItemAsync(datasetId, "transcript:task-1:0");
        var second = await SeedItemAsync(datasetId, "transcript:task-1:1000");
        var kept = await SeedItemAsync(datasetId, "transcript:task-1:2000");

        var result = await CreateController().BatchDelete(
            datasetId.ToString(),
            new TrainingDatasetBatchDeleteRequest(
            [
                first.Id.ToString(),
                "not-an-objectid",
                ObjectId.GenerateNewId().ToString(),
                second.Id.ToString(),
            ]));

        var dto = PayloadOf(result);
        Assert.Equal(2, dto.Deleted);

        var rest = Assert.Single(await Items.ListAsync(datasetId));
        Assert.Equal(kept.Id, rest.Id);
    }

    [Fact]
    public async Task BatchDelete_EmptyIds_ReturnsZeroDeleted()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "transcript:task-1:0");

        var result = await CreateController().BatchDelete(
            datasetId.ToString(), new TrainingDatasetBatchDeleteRequest([]));

        Assert.Equal(0, PayloadOf(result).Deleted);
        Assert.Single(await Items.ListAsync(datasetId));
    }

    [Fact]
    public async Task BatchDelete_UnknownOrInvalidDataset_Returns404()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "transcript:task-1:0");
        var controller = CreateController();
        var request = new TrainingDatasetBatchDeleteRequest([ObjectId.GenerateNewId().ToString()]);

        var unknown = await controller.BatchDelete(ObjectId.GenerateNewId().ToString(), request);
        Assert.Equal("dataset not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var invalid = await controller.BatchDelete("not-an-objectid", request);
        Assert.Equal("dataset not found", ErrorOf(invalid, StatusCodes.Status404NotFound));

        Assert.Single(await Items.ListAsync(datasetId));
    }

    /// <summary>导入客户端桩（导入端点用例在 <see cref="TrainingImportApiTests"/>）。</summary>
    private sealed class StubTranscriptClient : ITranscriptApiClient
    {
        public Task<TranscriptTaskDetail?> GetTaskDetailAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult<TranscriptTaskDetail?>(null);
    }
}
