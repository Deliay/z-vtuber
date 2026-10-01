using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.12：情绪识别任务 API 控制器直测（Testcontainers Mongo）——
/// 提交 <c>{created, skipped}</c>（重复提交/空文本/非法 id 跳过）、队列 <c>items</c>/<c>counts</c> 与
/// <c>datasetId</c>/<c>status</c>/<c>limit</c> 过滤、retry FAILED→PENDING（attempts 保留、error 清空）。
/// </summary>
public class EmotionTaskApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingModelRepository Models => new(fixture.Database);

    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingEmotionTaskRepository Tasks => new(fixture.Database);

    private TrainingItemToolsController CreateToolsController() =>
        WithContext(new TrainingItemToolsController(
            Datasets, Items, Models, Providers,
            new EmotionTaskStubAudioClient(), new EmotionTaskStubChatFactory(), Tasks,
            NullLogger<TrainingItemToolsController>.Instance));

    private TrainingEmotionTasksController CreateQueueController() =>
        WithContext(new TrainingEmotionTasksController(
            Tasks, NullLogger<TrainingEmotionTasksController>.Instance));

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

    private sealed class EmotionTaskStubAudioClient : IRecordingAudioClient
    {
        public Task<AudioClip> GetClipAsync(
            string audioUri, long startMs, long? endMs, CancellationToken ct = default) =>
            throw new NotSupportedException("emotion submission does not fetch audio");

        public Task<AudioClip> MergeClipsAsync(
            IReadOnlyList<AudioClipRequest> items, CancellationToken ct = default) =>
            throw new NotSupportedException("emotion submission does not merge audio");
    }

    private sealed class EmotionTaskStubChatFactory : IChatModelClientFactory
    {
        public IChatModelClient Create(string providerType) => throw new NotSupportedException();
    }

    private async Task<ObjectId> SeedDatasetAsync(string name = "音频数据集")
    {
        var now = DateTime.UtcNow;
        var record = await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = $"{name}-{Guid.NewGuid():N}", Type = "audio", CreatedAt = now, UpdatedAt = now,
        });
        return record.Id;
    }

    private async Task<(TrainingProviderRecord Provider, TrainingModelRecord Model)> SeedModelAsync(
        bool enabled = true)
    {
        var now = DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"provider-{Guid.NewGuid():N}", Type = "openai", Endpoint = "http://llm",
            ApiKey = "secret", CreatedAt = now, UpdatedAt = now,
        });
        var model = await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "gpt-test", Enabled = enabled,
            CreatedAt = now, UpdatedAt = now,
        });
        return (provider, model);
    }

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(
        ObjectId datasetId, string text = "有文本")
    {
        var now = DateTime.UtcNow;
        var item = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task-1",
            AudioUri = "b/live/1/recordings/a.flv",
            AudioKey = $"transcript:task-1:{Guid.NewGuid():N}",
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([item]);
        return item;
    }

    private async Task<TrainingEmotionTaskRecord> SeedTaskAsync(
        ObjectId datasetId,
        ObjectId? itemId = null,
        string status = TrainingEmotionTaskStatus.Pending,
        int attempts = 0,
        string? error = null,
        DateTime? createdAt = null)
    {
        var task = new TrainingEmotionTaskRecord
        {
            DatasetId = datasetId,
            ItemId = itemId ?? ObjectId.GenerateNewId(),
            AudioKey = "transcript:task-1:0",
            ModelId = ObjectId.GenerateNewId(),
            ModelSlug = "provider/gpt-test",
            Status = status,
            Attempts = attempts,
            Error = error,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        await Tasks.InsertAsync(task);
        return task;
    }

    // ---- 提交（接口 12）----

    [Fact]
    public async Task CreateEmotionTasks_CountsCreatedAndSkipped()
    {
        var datasetId = await SeedDatasetAsync();
        var (provider, model) = await SeedModelAsync();
        var created = await SeedItemAsync(datasetId, "要识别的文本");
        var emptyText = await SeedItemAsync(datasetId, "   ");
        var active = await SeedItemAsync(datasetId, "已有任务");
        await SeedTaskAsync(datasetId, active.Id, TrainingEmotionTaskStatus.Pending);

        var request = new TrainingEmotionTaskCreateRequest(
            [created.Id.ToString(), emptyText.Id.ToString(), active.Id.ToString(), "not-an-objectid", ""],
            model.Id.ToString());

        var result = PayloadOf(await CreateToolsController().CreateEmotionTasks(datasetId.ToString(), request));

        Assert.Equal(1, result.Created);
        Assert.Equal(4, result.Skipped);

        var all = await Tasks.ListAsync(datasetId, null, 100);
        Assert.Equal(2, all.Count);
        var stored = Assert.Single(all, t => t.ItemId == created.Id);
        Assert.Equal(datasetId, stored.DatasetId);
        Assert.Equal(created.AudioKey, stored.AudioKey);
        Assert.Equal(model.Id, stored.ModelId);
        Assert.Equal($"{provider.Name}/gpt-test", stored.ModelSlug);
        Assert.Equal(TrainingEmotionTaskStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task CreateEmotionTasks_RepeatSubmit_IsSkipped()
    {
        var datasetId = await SeedDatasetAsync();
        var (_, model) = await SeedModelAsync();
        var item = await SeedItemAsync(datasetId);
        var request = new TrainingEmotionTaskCreateRequest(
            [item.Id.ToString()], model.Id.ToString());
        var controller = CreateToolsController();

        Assert.Equal(1, PayloadOf(await controller.CreateEmotionTasks(datasetId.ToString(), request)).Created);

        var second = PayloadOf(await controller.CreateEmotionTasks(datasetId.ToString(), request));
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Skipped);
        Assert.Single(await Tasks.ListAsync(datasetId, null, 100));
    }

    [Fact]
    public async Task CreateEmotionTasks_DisabledModel_Returns400ModelDisabled()
    {
        var datasetId = await SeedDatasetAsync();
        var (_, model) = await SeedModelAsync(enabled: false);
        var item = await SeedItemAsync(datasetId);

        var result = await CreateToolsController().CreateEmotionTasks(
            datasetId.ToString(),
            new TrainingEmotionTaskCreateRequest([item.Id.ToString()], model.Id.ToString()));

        Assert.Equal("model disabled", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Tasks.ListAsync(datasetId, null, 100));
    }

    [Fact]
    public async Task CreateEmotionTasks_ConcurrentRepeatSubmit_OnlyOneCreated()
    {
        var datasetId = await SeedDatasetAsync();
        var (_, model) = await SeedModelAsync();
        var item = await SeedItemAsync(datasetId);
        var request = new TrainingEmotionTaskCreateRequest(
            [item.Id.ToString()], model.Id.ToString());

        // 并发（双击/双副本）绕过 HasActive 预检：唯一索引兜底，只允许建一条活跃任务
        var results = await Task.WhenAll(
            CreateToolsController().CreateEmotionTasks(datasetId.ToString(), request),
            CreateToolsController().CreateEmotionTasks(datasetId.ToString(), request));

        var created = results.Sum(r => PayloadOf(r).Created);
        Assert.Equal(1, created);
        Assert.Single(await Tasks.ListAsync(datasetId, null, 100));
    }

    [Fact]
    public async Task CreateEmotionTasks_EmptyItemIds_Returns400()
    {
        var datasetId = await SeedDatasetAsync();
        var (_, model) = await SeedModelAsync();

        var empty = await CreateToolsController().CreateEmotionTasks(
            datasetId.ToString(), new TrainingEmotionTaskCreateRequest([], model.Id.ToString()));
        Assert.Equal("itemIds is required", ErrorOf(empty, StatusCodes.Status400BadRequest));

        var missing = await CreateToolsController().CreateEmotionTasks(
            datasetId.ToString(), new TrainingEmotionTaskCreateRequest(null, model.Id.ToString()));
        Assert.Equal("itemIds is required", ErrorOf(missing, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task CreateEmotionTasks_UnknownDatasetOrModel_Returns404()
    {
        var datasetId = await SeedDatasetAsync();
        var (_, model) = await SeedModelAsync();

        var unknownDataset = await CreateToolsController().CreateEmotionTasks(
            ObjectId.GenerateNewId().ToString(),
            new TrainingEmotionTaskCreateRequest([ObjectId.GenerateNewId().ToString()], model.Id.ToString()));
        Assert.Equal("dataset not found", ErrorOf(unknownDataset, StatusCodes.Status404NotFound));

        var unknownModel = await CreateToolsController().CreateEmotionTasks(
            datasetId.ToString(),
            new TrainingEmotionTaskCreateRequest([ObjectId.GenerateNewId().ToString()],
                ObjectId.GenerateNewId().ToString()));
        Assert.Equal("model not found", ErrorOf(unknownModel, StatusCodes.Status404NotFound));
    }

    // ---- 队列（接口 13）----

    [Fact]
    public async Task List_ReturnsItemsAndCounts_ForDataset()
    {
        var datasetId = await SeedDatasetAsync();
        var other = await SeedDatasetAsync("其他");
        var baseTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Pending, createdAt: baseTime);
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Pending, createdAt: baseTime.AddMinutes(1));
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Done, createdAt: baseTime.AddMinutes(2));
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Failed, createdAt: baseTime.AddMinutes(3));
        await SeedTaskAsync(other, status: TrainingEmotionTaskStatus.Pending, createdAt: baseTime.AddMinutes(4));

        var result = PayloadOf(await CreateQueueController().List(datasetId.ToString(), null, null));

        Assert.Equal(4, result.Items.Count);
        // CreatedAt 降序
        Assert.True(result.Items[0].CreatedAt > result.Items[1].CreatedAt);
        Assert.Equal(2, result.Counts.Pending);
        Assert.Equal(0, result.Counts.Running);
        Assert.Equal(1, result.Counts.Done);
        Assert.Equal(1, result.Counts.Failed);
        Assert.All(result.Items, i => Assert.Equal("provider/gpt-test", i.ModelSlug));
    }

    [Fact]
    public async Task List_StatusFilter_NormalizesCase_AndCountsIgnoreStatus()
    {
        var datasetId = await SeedDatasetAsync();
        var baseTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Pending, createdAt: baseTime);
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Failed, createdAt: baseTime.AddMinutes(1));

        var lower = PayloadOf(await CreateQueueController().List(datasetId.ToString(), "failed", null));
        Assert.Equal("failed", Assert.Single(lower.Items).Status);
        // counts 为 datasetId 全量分组，不受 status 影响
        Assert.Equal(1, lower.Counts.Pending);
        Assert.Equal(1, lower.Counts.Failed);

        var upper = PayloadOf(await CreateQueueController().List(datasetId.ToString(), "PENDING", null));
        Assert.Equal("pending", Assert.Single(upper.Items).Status);

        var none = PayloadOf(await CreateQueueController().List(datasetId.ToString(), "running", null));
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task List_Limit_ReturnsNewestFirst()
    {
        var datasetId = await SeedDatasetAsync();
        var baseTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Pending, createdAt: baseTime);
        var newest = await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Done,
            createdAt: baseTime.AddMinutes(5));

        var result = PayloadOf(await CreateQueueController().List(datasetId.ToString(), null, 1));

        var only = Assert.Single(result.Items);
        Assert.Equal(newest.Id.ToString(), only.Id);
        // counts 仍为全量
        Assert.Equal(1, result.Counts.Pending);
        Assert.Equal(1, result.Counts.Done);
    }

    [Fact]
    public async Task List_InvalidLimit_FallsBackToDefault()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedTaskAsync(datasetId);

        var result = PayloadOf(await CreateQueueController().List(datasetId.ToString(), null, 0));

        Assert.Single(result.Items);
    }

    // ---- 重试（接口 14）----

    [Fact]
    public async Task Retry_FailedTask_ResetsToPendingKeepingAttemptsAndClearingError()
    {
        var datasetId = await SeedDatasetAsync();
        var task = await SeedTaskAsync(datasetId, status: TrainingEmotionTaskStatus.Failed,
            attempts: 2, error: "model service unavailable");
        // 模拟已执行过：补 StartedAt/FinishedAt
        await Tasks.UpdateAsync(task.Id, MongoDB.Driver.Builders<TrainingEmotionTaskRecord>.Update
            .Set(t => t.StartedAt, DateTime.UtcNow)
            .Set(t => t.FinishedAt, DateTime.UtcNow));

        var result = PayloadOf(await CreateQueueController().Retry(task.Id.ToString()));

        Assert.Equal("pending", result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Null(result.Error);
        Assert.Null(result.StartedAt);
        Assert.Null(result.FinishedAt);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Pending, stored!.Status);
        Assert.Equal(2, stored.Attempts);
        Assert.Null(stored.Error);
    }

    [Theory]
    [InlineData(TrainingEmotionTaskStatus.Pending)]
    [InlineData(TrainingEmotionTaskStatus.Running)]
    [InlineData(TrainingEmotionTaskStatus.Done)]
    public async Task Retry_NonFailed_Returns400TaskNotFailed(string status)
    {
        var datasetId = await SeedDatasetAsync();
        var task = await SeedTaskAsync(datasetId, status: status);

        var result = await CreateQueueController().Retry(task.Id.ToString());

        Assert.Equal("task not failed", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Equal(status, (await Tasks.GetByIdAsync(task.Id))!.Status);
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("62a2a2a2a2a2a2a2a2a2a2a2")]
    public async Task Retry_InvalidOrUnknown_Returns404(string taskId)
    {
        var result = await CreateQueueController().Retry(taskId);

        Assert.Equal("task not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Retry_WhenAnotherActiveTaskExists_Returns409()
    {
        var datasetId = await SeedDatasetAsync();
        var itemId = ObjectId.GenerateNewId();
        var failed = await SeedTaskAsync(datasetId, itemId, TrainingEmotionTaskStatus.Failed);
        // 同条目已有活跃（PENDING）任务：原子重试命中唯一索引冲突，不得同时置回 PENDING
        await SeedTaskAsync(datasetId, itemId, TrainingEmotionTaskStatus.Pending);

        var result = await CreateQueueController().Retry(failed.Id.ToString());

        Assert.Equal("active task exists", ErrorOf(result, StatusCodes.Status409Conflict));
        Assert.Equal(TrainingEmotionTaskStatus.Failed, (await Tasks.GetByIdAsync(failed.Id))!.Status);
    }
}
