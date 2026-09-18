using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
using Z.Vtuber.RecordingTranscript.Consumer.Tasks;

namespace Z.Vtuber.Tests;

public class TranscriptTaskScannerTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptTaskRepository Tasks => new(mongo.Database);
    private TranscriptModelRepository Models => new(mongo.Database);

    private async Task<ObjectId> InsertModelAsync(int maxConcurrency)
    {
        var model = new TranscriptModelRecord
        {
            Name = "model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key",
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = maxConcurrency,
            CreatedAt = DateTime.UtcNow,
        };
        await Models.InsertAsync(model);
        return model.Id;
    }

    private async Task<ObjectId> InsertTaskAsync(
        ObjectId modelId, string status, long roomId = 100)
    {
        var now = DateTime.UtcNow;
        var task = new TranscriptTaskRecord
        {
            RecordingId = ObjectId.GenerateNewId(),
            RoomId = roomId,
            File = "[RAW] a.flv",
            ModelId = modelId,
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Manual,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Tasks.InsertAsync(task);
        return task.Id;
    }

    private TranscriptTaskScanner CreateScanner(
        FakeTranscriptTaskLockProvider lockProvider, FakeTranscriptTaskRunner runner) =>
        new(Tasks, Models, lockProvider, runner,
            new TranscriptConsumerOptions { ScanBatchSize = 50 },
            NullLogger<TranscriptTaskScanner>.Instance);

    private async Task RunAndReleaseAsync(TranscriptTaskScanner scanner, FakeTranscriptTaskRunner runner)
    {
        try
        {
            await scanner.ScanOnceAsync();
        }
        finally
        {
            foreach (var gate in runner.Gates)
            {
                gate.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task ZombieRunning_WithoutLock_IsTakenOver()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 1);
        var taskId = await InsertTaskAsync(modelId, TranscriptTaskStatus.Running);

        var lockProvider = new FakeTranscriptTaskLockProvider();
        var runner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var scanner = CreateScanner(lockProvider, runner);

        await RunAndReleaseAsync(scanner, runner);

        Assert.Contains(taskId, runner.RunTaskIds);
        Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task ZombieRunning_DoesNotStarveSameModelPending()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 1);
        var zombie = await InsertTaskAsync(modelId, TranscriptTaskStatus.Running);
        var pending = await InsertTaskAsync(modelId, TranscriptTaskStatus.Pending);

        var lockProvider = new FakeTranscriptTaskLockProvider();
        var runner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var scanner = CreateScanner(lockProvider, runner);

        await RunAndReleaseAsync(scanner, runner);

        // 僵尸先被接管并占住额度（max=1），PENDING 本轮等待
        Assert.Contains(zombie, runner.RunTaskIds);
        Assert.DoesNotContain(pending, runner.RunTaskIds);

        // 僵尸完成后，PENDING 不被永久饿死
        await Tasks.SetStatusAsync(zombie, TranscriptTaskStatus.Done);
        lockProvider.HeldIds.Remove(zombie);
        runner.RunTaskIds.Clear();
        await RunAndReleaseAsync(scanner, runner);

        Assert.Contains(pending, runner.RunTaskIds);
    }

    [Fact]
    public async Task HeldRunning_StillOccupiesConcurrency()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 1);
        var held = await InsertTaskAsync(modelId, TranscriptTaskStatus.Running);
        var pending = await InsertTaskAsync(modelId, TranscriptTaskStatus.Pending);

        var lockProvider = new FakeTranscriptTaskLockProvider();
        lockProvider.HeldIds.Add(held); // 他节点正在执行
        var runner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var scanner = CreateScanner(lockProvider, runner);

        await RunAndReleaseAsync(scanner, runner);

        Assert.Empty(runner.RunTaskIds);
        Assert.Equal(TranscriptTaskStatus.Pending, (await Tasks.GetByIdAsync(pending))!.Status);
    }

    [Fact]
    public async Task MultipleZombies_AreTakenWithinConcurrency_NoPermanentStarvation()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 2);
        var first = await InsertTaskAsync(modelId, TranscriptTaskStatus.Running, roomId: 101);
        var second = await InsertTaskAsync(modelId, TranscriptTaskStatus.Running, roomId: 102);
        var third = await InsertTaskAsync(modelId, TranscriptTaskStatus.Running, roomId: 103);

        var lockProvider = new FakeTranscriptTaskLockProvider();
        var runner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var scanner = CreateScanner(lockProvider, runner);

        await RunAndReleaseAsync(scanner, runner);

        Assert.Equal(2, runner.RunTaskIds.Count);
        Assert.Contains(first, runner.RunTaskIds);
        Assert.Contains(second, runner.RunTaskIds);
        Assert.DoesNotContain(third, runner.RunTaskIds);

        // 释放前两个额度后，第三个仍可被接管
        await Tasks.SetStatusAsync(first, TranscriptTaskStatus.Done);
        await Tasks.SetStatusAsync(second, TranscriptTaskStatus.Done);
        lockProvider.HeldIds.Remove(first);
        lockProvider.HeldIds.Remove(second);
        runner.RunTaskIds.Clear();
        await RunAndReleaseAsync(scanner, runner);

        Assert.Contains(third, runner.RunTaskIds);
    }

    [Fact]
    public async Task RetriedFailedTask_PlacedRunningWithoutLock_IsTakenOver()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 1);
        var taskId = await InsertTaskAsync(modelId, TranscriptTaskStatus.Failed);

        // 模拟 retry 端点：置 RUNNING、无锁
        await Tasks.SetStatusAsync(taskId, TranscriptTaskStatus.Running, null);

        var lockProvider = new FakeTranscriptTaskLockProvider();
        var runner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var scanner = CreateScanner(lockProvider, runner);

        await RunAndReleaseAsync(scanner, runner);

        Assert.Contains(taskId, runner.RunTaskIds);
    }
}
