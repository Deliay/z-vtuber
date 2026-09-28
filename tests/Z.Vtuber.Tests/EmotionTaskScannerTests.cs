using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Emotion;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.8：<see cref="EmotionTaskScanner"/> 直测（Testcontainers Mongo + 假锁 + 假 Runner）——
/// 领取 PENDING、跳过占锁 RUNNING、接管无锁僵尸 RUNNING、MaxAttempts 超限 FAILED、
/// 扫描异常不中断循环、全局并发门控。
/// </summary>
public class EmotionTaskScannerTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingEmotionTaskRepository Tasks => new(mongo.Database);

    private sealed class EmotionScannerFakeLock : IEmotionTaskLock
    {
        public ObjectId TaskId { get; set; } = ObjectId.GenerateNewId();
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public bool Lost => false;
        public Action? OnRelease { get; set; }

        public Task<bool> CheckOwnershipAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RenewAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task ReleaseAsync(CancellationToken ct = default)
        {
            OnRelease?.Invoke();
            return Task.CompletedTask;
        }

        public void StartRenewal(CancellationToken ct)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmotionScannerFakeLockProvider : IEmotionTaskLockProvider
    {
        public bool AcquireResult { get; set; } = true;
        public HashSet<ObjectId> HeldIds { get; } = [];
        public bool ThrowOnFirstAcquire { get; set; }
        private int _acquireAttempts;

        public Task<IEmotionTaskLock?> TryAcquireAsync(ObjectId taskId, CancellationToken ct = default)
        {
            if (ThrowOnFirstAcquire && Interlocked.Increment(ref _acquireAttempts) == 1)
            {
                throw new InvalidOperationException("redis down");
            }

            if (!AcquireResult)
            {
                return Task.FromResult<IEmotionTaskLock?>(null);
            }

            var handle = new EmotionScannerFakeLock
            {
                TaskId = taskId,
                OnRelease = () => HeldIds.Remove(taskId),
            };
            HeldIds.Add(taskId);
            return Task.FromResult<IEmotionTaskLock?>(handle);
        }

        public Task<bool> IsHeldAsync(ObjectId taskId, CancellationToken ct = default) =>
            Task.FromResult(HeldIds.Contains(taskId));
    }

    private sealed class EmotionScannerNullChatFactory : IChatModelClientFactory
    {
        public IChatModelClient Create(string providerType) => throw new NotSupportedException();
    }

    /// <summary>记录型假 Runner：继承真实 Runner、覆盖 RunAsync（生产按具体类型注册，扫描器依赖具体类型以适配 DI）。</summary>
    private sealed class EmotionScannerFakeRunner(
        TrainingEmotionTaskRepository tasks,
        TrainingDatasetItemRepository items,
        TrainingModelRepository models,
        TrainingProviderRepository providers)
        : EmotionTaskRunner(
            tasks, items, models, providers,
            new EmotionScannerNullChatFactory(), new EmotionTaskOptions(),
            NullLogger<EmotionTaskRunner>.Instance)
    {
        public List<ObjectId> RunTaskIds { get; } = [];
        public List<TaskCompletionSource> Gates { get; } = [];
        public bool WaitForGate { get; set; }

        public override async Task RunAsync(
            TrainingEmotionTaskRecord task, IEmotionTaskLock handle, CancellationToken ct = default)
        {
            RunTaskIds.Add(task.Id);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Gates.Add(gate);
            if (WaitForGate)
            {
                await gate.Task.WaitAsync(ct);
            }

            await handle.ReleaseAsync(ct);
            await handle.DisposeAsync();
        }
    }

    private EmotionScannerFakeRunner CreateFakeRunner() =>
        new(Tasks, new TrainingDatasetItemRepository(mongo.Database),
            new TrainingModelRepository(mongo.Database), new TrainingProviderRepository(mongo.Database));

    private EmotionTaskScanner CreateScanner(
        EmotionScannerFakeLockProvider lockProvider,
        EmotionScannerFakeRunner runner,
        EmotionTaskOptions? options = null) =>
        new(Tasks, lockProvider, runner,
            options ?? new EmotionTaskOptions { ScanBatchSize = 50 },
            NullLogger<EmotionTaskScanner>.Instance);

    private async Task<ObjectId> InsertTaskAsync(
        string status = TrainingEmotionTaskStatus.Pending,
        int attempts = 0,
        DateTime? createdAt = null)
    {
        var task = new TrainingEmotionTaskRecord
        {
            DatasetId = ObjectId.GenerateNewId(),
            ItemId = ObjectId.GenerateNewId(),
            AudioKey = "transcript:task-1:0",
            ModelId = ObjectId.GenerateNewId(),
            ModelSlug = "provider/gpt-test",
            Status = status,
            Attempts = attempts,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        await Tasks.InsertAsync(task);
        return task.Id;
    }

    private static async Task ReleaseGatesAsync(EmotionScannerFakeRunner runner)
    {
        foreach (var gate in runner.Gates)
        {
            gate.TrySetResult();
        }

        await Task.Delay(50);
    }

    [Fact]
    public async Task Pending_IsScannedAndRun()
    {
        var taskId = await InsertTaskAsync();
        var lockProvider = new EmotionScannerFakeLockProvider();
        var runner = CreateFakeRunner();
        var scanner = CreateScanner(lockProvider, runner);

        await scanner.ScanOnceAsync();
        await ReleaseGatesAsync(runner);

        Assert.Contains(taskId, runner.RunTaskIds);
        var stored = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TrainingEmotionTaskStatus.Running, stored!.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.StartedAt);
    }

    [Fact]
    public async Task HeldRunning_IsSkipped()
    {
        var taskId = await InsertTaskAsync(TrainingEmotionTaskStatus.Running);
        var lockProvider = new EmotionScannerFakeLockProvider();
        lockProvider.HeldIds.Add(taskId); // 他节点正在执行
        var runner = CreateFakeRunner();
        var scanner = CreateScanner(lockProvider, runner);

        await scanner.ScanOnceAsync();

        Assert.Empty(runner.RunTaskIds);
        var stored = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TrainingEmotionTaskStatus.Running, stored!.Status);
        Assert.Equal(0, stored.Attempts);
    }

    [Fact]
    public async Task ZombieRunning_WithoutLock_IsTakenOver()
    {
        var taskId = await InsertTaskAsync(TrainingEmotionTaskStatus.Running);
        var lockProvider = new EmotionScannerFakeLockProvider();
        var runner = CreateFakeRunner();
        var scanner = CreateScanner(lockProvider, runner);

        await scanner.ScanOnceAsync();
        await ReleaseGatesAsync(runner);

        Assert.Contains(taskId, runner.RunTaskIds);
        Assert.Equal(1, (await Tasks.GetByIdAsync(taskId))!.Attempts);
    }

    [Fact]
    public async Task ZombieRunning_AttemptsAtMax_MarksFailedWithoutRunning()
    {
        // 返工 #6：MaxAttempts 仅约束僵尸 RUNNING 接管；无锁 RUNNING 且次数耗尽 → FAILED
        var taskId = await InsertTaskAsync(TrainingEmotionTaskStatus.Running, attempts: 1);
        var lockProvider = new EmotionScannerFakeLockProvider();
        var runner = CreateFakeRunner();
        var scanner = CreateScanner(lockProvider, runner, new EmotionTaskOptions { MaxAttempts = 1 });

        await scanner.ScanOnceAsync();

        var stored = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TrainingEmotionTaskStatus.Failed, stored!.Status);
        Assert.Equal("max attempts exceeded", stored.Error);
        Assert.Empty(runner.RunTaskIds);
    }

    [Fact]
    public async Task PendingAtMaxAttempts_IsRun_SoManualRetryWorks()
    {
        // 返工 #6：人工 retry 保留 attempts 并置 PENDING，耗尽次数的 PENDING 仍须执行（失败可重试）
        var taskId = await InsertTaskAsync(TrainingEmotionTaskStatus.Pending, attempts: 3);
        var lockProvider = new EmotionScannerFakeLockProvider();
        var runner = CreateFakeRunner();
        var scanner = CreateScanner(lockProvider, runner, new EmotionTaskOptions { MaxAttempts = 3 });

        await scanner.ScanOnceAsync();
        await ReleaseGatesAsync(runner);

        Assert.Contains(taskId, runner.RunTaskIds);
        Assert.Equal(4, (await Tasks.GetByIdAsync(taskId))!.Attempts);
    }

    [Fact]
    public async Task ConcurrencyGate_BoundsSimultaneousRuns()
    {
        var baseTime = DateTime.UtcNow;
        var first = await InsertTaskAsync(createdAt: baseTime);
        var second = await InsertTaskAsync(createdAt: baseTime.AddMilliseconds(1));
        var lockProvider = new EmotionScannerFakeLockProvider();
        var runner = CreateFakeRunner();
        runner.WaitForGate = true;
        var scanner = CreateScanner(
            lockProvider, runner,
            new EmotionTaskOptions { ScanBatchSize = 10, MaxConcurrentTasks = 1 });

        try
        {
            await scanner.ScanOnceAsync();

            // MaxConcurrentTasks=1：仅第一个任务被领取执行
            Assert.Single(runner.RunTaskIds);
            Assert.Contains(first, runner.RunTaskIds);
            Assert.Equal(TrainingEmotionTaskStatus.Pending, (await Tasks.GetByIdAsync(second))!.Status);
            Assert.Equal(TrainingEmotionTaskStatus.Running, (await Tasks.GetByIdAsync(first))!.Status);
        }
        finally
        {
            await ReleaseGatesAsync(runner);
        }

        // 第一个完成后释放额度，第二个可被领取
        await Tasks.UpdateAsync(
            first,
            MongoDB.Driver.Builders<TrainingEmotionTaskRecord>.Update
                .Set(t => t.Status, TrainingEmotionTaskStatus.Done));

        await scanner.ScanOnceAsync();
        await ReleaseGatesAsync(runner);

        Assert.Contains(second, runner.RunTaskIds);
    }

    [Fact]
    public async Task Loop_SurvivesScanException_AndProcessesNextRound()
    {
        var taskId = await InsertTaskAsync();
        var lockProvider = new EmotionScannerFakeLockProvider { ThrowOnFirstAcquire = true };
        var runner = CreateFakeRunner();
        var scanner = CreateScanner(
            lockProvider, runner,
            new EmotionTaskOptions { ScanBatchSize = 10, ScanIntervalSeconds = 1 });

        await scanner.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 100 && runner.RunTaskIds.Count == 0; i++)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            await scanner.StopAsync(CancellationToken.None);
        }

        // 首轮抢锁抛异常被 ExecuteAsync 吞掉，后续轮次正常领取
        Assert.Contains(taskId, runner.RunTaskIds);
    }
}
