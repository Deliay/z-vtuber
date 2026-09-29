using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Emotion;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.8：<see cref="EmotionTaskRunner"/> 直测（Testcontainers Mongo + 假模型客户端）——
/// 成功写条目 <c>Extensions.emotion</c> + 任务 DONE 快照、空文本/模型缺失/输出非法 FAILED、
/// 失锁不写保留 RUNNING、finally 释放锁。
/// </summary>
public class EmotionTaskRunnerTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingEmotionTaskRepository Tasks => new(mongo.Database);
    private TrainingDatasetItemRepository Items => new(mongo.Database);
    private TrainingModelRepository Models => new(mongo.Database);
    private TrainingProviderRepository Providers => new(mongo.Database);

    private const string ValidEmotionJson =
        "```json\n{\"emotionControlWeight\":0.6,\"emotionDescription\":\"低落\",\"happy\":0.05,\"angry\":0,"
        + "\"sad\":0.7,\"afraid\":0.05,\"disgusted\":0,\"melancholic\":0.8,\"surprised\":0.1,\"calm\":0.4}\n```";

    private sealed class EmotionRunnerStubChatClient : IChatModelClient
    {
        public string Raw { get; set; } = ValidEmotionJson;
        public Exception? Error { get; set; }
        public List<ChatPrompt> Prompts { get; } = [];

        public Task<string> CompleteJsonAsync(
            TrainingProviderRecord provider, TrainingModelRecord model,
            ChatPrompt prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Error is not null ? Task.FromException<string>(Error) : Task.FromResult(Raw);
        }
    }

    private sealed class EmotionRunnerStubChatFactory(EmotionRunnerStubChatClient client) : IChatModelClientFactory
    {
        public IChatModelClient Create(string providerType) => client;
    }

    private sealed class EmotionRunnerFakeLock : IEmotionTaskLock
    {
        public ObjectId TaskId { get; set; } = ObjectId.GenerateNewId();
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public bool Lost { get; private set; }
        public bool OwnershipResult { get; set; } = true;
        public bool Released { get; private set; }
        public int RenewalStarted;

        public Task<bool> CheckOwnershipAsync(CancellationToken ct = default)
        {
            if (!OwnershipResult)
            {
                Lost = true;
            }

            return Task.FromResult(OwnershipResult);
        }

        public Task<bool> RenewAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task ReleaseAsync(CancellationToken ct = default)
        {
            Released = true;
            return Task.CompletedTask;
        }

        public void StartRenewal(CancellationToken ct) => Interlocked.Increment(ref RenewalStarted);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private EmotionTaskRunner CreateRunner(EmotionRunnerStubChatClient client) =>
        new(Tasks, Items, Models, Providers,
            new EmotionRunnerStubChatFactory(client),
            new EmotionTaskOptions(),
            NullLogger<EmotionTaskRunner>.Instance);

    private async Task<ObjectId> InsertModelAsync(bool enabled = true)
    {
        var now = DateTime.UtcNow;
        var provider = new TrainingProviderRecord
        {
            Name = $"provider-{Guid.NewGuid():N}",
            Type = TrainingProviderTypes.OpenAi,
            Endpoint = "http://llm",
            ApiKey = "secret",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Providers.InsertAsync(provider);

        var model = new TrainingModelRecord
        {
            ProviderId = provider.Id,
            Model = "gpt-test",
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Models.InsertAsync(model);
        return model.Id;
    }

    private async Task<(ObjectId DatasetId, ObjectId ItemId)> InsertItemAsync(string text)
    {
        var datasetId = ObjectId.GenerateNewId();
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
        return (datasetId, item.Id);
    }

    private async Task<TrainingEmotionTaskRecord> InsertTaskAsync(
        ObjectId datasetId, ObjectId itemId, ObjectId modelId,
        string status = TrainingEmotionTaskStatus.Running)
    {
        var task = new TrainingEmotionTaskRecord
        {
            DatasetId = datasetId,
            ItemId = itemId,
            AudioKey = "transcript:task-1:0",
            ModelId = modelId,
            ModelSlug = "provider/gpt-test",
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };
        await Tasks.InsertAsync(task);
        return task;
    }

    [Fact]
    public async Task Run_Success_WritesEmotionAndMarksDone()
    {
        var modelId = await InsertModelAsync();
        var (datasetId, itemId) = await InsertItemAsync("我今天真的太难过了。");
        var task = await InsertTaskAsync(datasetId, itemId, modelId);
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id };

        await CreateRunner(new EmotionRunnerStubChatClient()).RunAsync(task, handle);

        var item = await Items.GetByIdAsync(datasetId, itemId);
        var emotion = item!.Extensions!["emotion"].AsBsonDocument;
        Assert.Equal(10, emotion.ElementCount);
        Assert.Equal("低落", emotion["emotionDescription"].AsString);
        foreach (var key in new[]
        {
            "happy", "angry", "sad", "afraid", "disgusted", "melancholic", "surprised", "calm",
        })
        {
            Assert.InRange(emotion[key].AsDouble, 0, 1);
        }

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Done, stored!.Status);
        Assert.NotNull(stored.Emotion);
        Assert.NotNull(stored.FinishedAt);
        Assert.Null(stored.Error);

        Assert.True(handle.Released);
        Assert.Equal(1, handle.RenewalStarted);
    }

    [Fact]
    public async Task Run_EmptyText_MarksFailed()
    {
        var modelId = await InsertModelAsync();
        var (datasetId, itemId) = await InsertItemAsync("   ");
        var task = await InsertTaskAsync(datasetId, itemId, modelId);
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id };
        var client = new EmotionRunnerStubChatClient();

        await CreateRunner(client).RunAsync(task, handle);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Failed, stored!.Status);
        Assert.Equal("item text is empty", stored.Error);
        Assert.Empty(client.Prompts);
        Assert.Null((await Items.GetByIdAsync(datasetId, itemId))!.Extensions);
    }

    [Fact]
    public async Task Run_ModelNotFound_MarksFailed()
    {
        var (datasetId, itemId) = await InsertItemAsync("有文本");
        var task = await InsertTaskAsync(datasetId, itemId, ObjectId.GenerateNewId());
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id };

        await CreateRunner(new EmotionRunnerStubChatClient()).RunAsync(task, handle);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Failed, stored!.Status);
        Assert.Equal("model not found", stored.Error);
        Assert.True(handle.Released);
    }

    [Fact]
    public async Task Run_ModelDisabled_MarksFailedWithoutCallingModel()
    {
        var modelId = await InsertModelAsync(enabled: false);
        var (datasetId, itemId) = await InsertItemAsync("有文本");
        var task = await InsertTaskAsync(datasetId, itemId, modelId);
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id };
        var client = new EmotionRunnerStubChatClient();

        await CreateRunner(client).RunAsync(task, handle);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Failed, stored!.Status);
        Assert.Equal("model disabled", stored.Error);
        Assert.Empty(client.Prompts);
        Assert.Null((await Items.GetByIdAsync(datasetId, itemId))!.Extensions);
    }

    [Fact]
    public async Task Run_AttemptTakenOverBeforeWrite_DoesNotWriteAndKeepsRunning()
    {
        var modelId = await InsertModelAsync();
        var (datasetId, itemId) = await InsertItemAsync("有文本");
        var seeded = await InsertTaskAsync(datasetId, itemId, modelId);
        // 模拟另一节点已接管并把 Attempts 推进到 1；本节点持陈旧 Attempts=0
        await Tasks.UpdateAsync(seeded.Id,
            MongoDB.Driver.Builders<TrainingEmotionTaskRecord>.Update.Set(t => t.Attempts, 1));
        var stale = new TrainingEmotionTaskRecord
        {
            Id = seeded.Id,
            DatasetId = datasetId,
            ItemId = itemId,
            ModelId = modelId,
            Status = TrainingEmotionTaskStatus.Running,
            Attempts = 0,
            CreatedAt = seeded.CreatedAt,
        };
        var handle = new EmotionRunnerFakeLock { TaskId = seeded.Id };

        await CreateRunner(new EmotionRunnerStubChatClient()).RunAsync(stale, handle);

        // fencing 落到写操作：陈旧领取者不得写条目、不得改状态
        var stored = await Tasks.GetByIdAsync(seeded.Id);
        Assert.Equal(1, stored!.Attempts);
        Assert.Equal(TrainingEmotionTaskStatus.Running, stored.Status);
        Assert.Null(stored.Emotion);
        Assert.Null((await Items.GetByIdAsync(datasetId, itemId))!.Extensions);
    }

    [Fact]
    public async Task Run_InvalidModelOutput_MarksFailed()
    {
        var modelId = await InsertModelAsync();
        var (datasetId, itemId) = await InsertItemAsync("有文本");
        var task = await InsertTaskAsync(datasetId, itemId, modelId);
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id };
        var client = new EmotionRunnerStubChatClient { Raw = "抱歉，我无法输出 JSON" };

        await CreateRunner(client).RunAsync(task, handle);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Failed, stored!.Status);
        Assert.Equal("invalid model output", stored.Error);
        // 输出非法不得写条目
        Assert.Null((await Items.GetByIdAsync(datasetId, itemId))!.Extensions);
    }

    [Fact]
    public async Task Run_TransportFailure_MarksFailedWithFriendlyMessage()
    {
        var modelId = await InsertModelAsync();
        var (datasetId, itemId) = await InsertItemAsync("有文本");
        var task = await InsertTaskAsync(datasetId, itemId, modelId);
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id };
        var client = new EmotionRunnerStubChatClient { Error = new HttpRequestException("boom") };

        await CreateRunner(client).RunAsync(task, handle);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Failed, stored!.Status);
        Assert.Equal("model service unavailable", stored.Error);
        Assert.True(handle.Released);
    }

    [Fact]
    public async Task Run_SingleFailureDoesNotAffectOthers()
    {
        var modelId = await InsertModelAsync();
        var (failedDataset, failedItem) = await InsertItemAsync("");
        var (doneDataset, doneItem) = await InsertItemAsync("正常文本");
        var failedTask = await InsertTaskAsync(failedDataset, failedItem, modelId);
        var doneTask = await InsertTaskAsync(doneDataset, doneItem, modelId);

        var runner = CreateRunner(new EmotionRunnerStubChatClient());
        await runner.RunAsync(failedTask, new EmotionRunnerFakeLock { TaskId = failedTask.Id });
        await runner.RunAsync(doneTask, new EmotionRunnerFakeLock { TaskId = doneTask.Id });

        Assert.Equal(TrainingEmotionTaskStatus.Failed, (await Tasks.GetByIdAsync(failedTask.Id))!.Status);
        Assert.Equal(TrainingEmotionTaskStatus.Done, (await Tasks.GetByIdAsync(doneTask.Id))!.Status);
        Assert.Null((await Items.GetByIdAsync(failedDataset, failedItem))!.Extensions);
        Assert.NotNull((await Items.GetByIdAsync(doneDataset, doneItem))!.Extensions);
    }

    [Fact]
    public async Task Run_LostLock_DoesNotWriteAndKeepsRunning()
    {
        var modelId = await InsertModelAsync();
        var (datasetId, itemId) = await InsertItemAsync("有文本");
        // 模拟扫描器已置 RUNNING；本节点随即失锁
        var task = await InsertTaskAsync(datasetId, itemId, modelId, TrainingEmotionTaskStatus.Running);
        var handle = new EmotionRunnerFakeLock { TaskId = task.Id, OwnershipResult = false };

        await CreateRunner(new EmotionRunnerStubChatClient()).RunAsync(task, handle);

        var stored = await Tasks.GetByIdAsync(task.Id);
        Assert.Equal(TrainingEmotionTaskStatus.Running, stored!.Status);
        Assert.Null(stored.Emotion);
        Assert.Null((await Items.GetByIdAsync(datasetId, itemId))!.Extensions);
        Assert.True(handle.Released);
    }
}
