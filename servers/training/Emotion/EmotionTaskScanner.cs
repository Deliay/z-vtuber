using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Emotion;

/// <summary>
/// 周期扫描（HostedService）：PENDING 或 RUNNING 但锁不存在（上一节点死亡）的任务领取，
/// 全局 <see cref="EmotionTaskOptions.MaxConcurrentTasks"/> 信号量门控；抢锁成功置 RUNNING 后交 Runner 执行。
/// </summary>
/// <remarks>
/// 并发门控口径：<c>MaxConcurrentTasks</c> 为全局上限（无按模型并发），采用「信号量持有整段执行期」方案——
/// <c>WaitAsync(0)</c> 无额度即结束本轮（不阻塞循环），额度在 detached 运行结束的 finally 中释放。
/// 相比「按持锁 RUNNING 计数」，该方案不依赖跨节点 Redis 查询、无自我死锁，简单可靠。
/// </remarks>
public sealed class EmotionTaskScanner(
    TrainingEmotionTaskRepository tasks,
    IEmotionTaskLockProvider lockProvider,
    EmotionTaskRunner runner,
    EmotionTaskOptions options,
    ILogger<EmotionTaskScanner> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.MaxConcurrentTasks));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动抖动：多副本同时启动时错开首轮扫描，降低抢锁惊群
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Emotion task scan iteration failed; continuing.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task ScanOnceAsync(CancellationToken ct = default)
    {
        var scannable = await tasks.ListClaimableAsync(options.ScanBatchSize, ct);
        foreach (var task in scannable)
        {
            ct.ThrowIfCancellationRequested();

            if (string.Equals(task.Status, TrainingEmotionTaskStatus.Running, StringComparison.Ordinal)
                && await lockProvider.IsHeldAsync(task.Id, ct))
            {
                // 他节点正在处理该 RUNNING 任务
                continue;
            }

            // MaxAttempts 仅约束「僵尸 RUNNING 接管」；PENDING（含人工 retry 保留 attempts 的任务）放行执行，
            // 否则耗尽次数的任务点「重试」后会立即再次失败，违反「失败可重试」边界。
            if (string.Equals(task.Status, TrainingEmotionTaskStatus.Running, StringComparison.Ordinal)
                && task.Attempts >= options.MaxAttempts)
            {
                await SetFailedAsync(task.Id, "max attempts exceeded", ct);
                continue;
            }

            // 全局并发门控：无额度即结束本轮（不阻塞扫描循环）
            if (!await _gate.WaitAsync(0, ct))
            {
                break;
            }

            IEmotionTaskLock? handle;
            try
            {
                handle = await lockProvider.TryAcquireAsync(task.Id, ct);
            }
            catch
            {
                _gate.Release();
                throw;
            }

            if (handle is null)
            {
                _gate.Release();
                continue;
            }

            TrainingEmotionTaskRecord claimed;
            try
            {
                var update = Builders<TrainingEmotionTaskRecord>.Update
                    .Set(t => t.Attempts, task.Attempts + 1)
                    .Set(t => t.Status, TrainingEmotionTaskStatus.Running)
                    .Set(t => t.StartedAt, DateTime.UtcNow);
                var updated = await tasks.UpdateAsync(task.Id, update, ct);
                if (updated is null)
                {
                    _gate.Release();
                    await SafeReleaseLockAsync(handle, task.Id);
                    continue;
                }

                // 交给 Runner 的是「领取后」的记录（Attempts 已累加），使 Runner 的 fencing 条件
                // （Id + Status==RUNNING + Attempts）能与库中当前领取者精确匹配。
                claimed = updated;
            }
            catch (Exception ex)
            {
                // 置 RUNNING 失败：释放额度 + 锁，避免并发泄漏与僵尸锁
                logger.LogWarning(ex, "Failed to mark emotion task {TaskId} as RUNNING", task.Id);
                _gate.Release();
                await SafeReleaseLockAsync(handle, task.Id);
                continue;
            }

            // 锁由 Runner 持有并在 finally 释放；额度在运行结束时释放，从而全局限流
            _ = RunDetachedAsync(claimed, handle, ct);
        }
    }

    private async Task RunDetachedAsync(
        TrainingEmotionTaskRecord task, IEmotionTaskLock handle, CancellationToken ct)
    {
        try
        {
            await runner.RunAsync(task, handle, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Emotion task runner for {TaskId} threw", task.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetFailedAsync(ObjectId taskId, string error, CancellationToken ct)
    {
        try
        {
            var update = Builders<TrainingEmotionTaskRecord>.Update
                .Set(t => t.Status, TrainingEmotionTaskStatus.Failed)
                .Set(t => t.Error, error)
                .Set(t => t.FinishedAt, DateTime.UtcNow);
            await tasks.UpdateAsync(taskId, update, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark emotion task {TaskId} as FAILED", taskId);
        }
    }

    private async Task SafeReleaseLockAsync(IEmotionTaskLock handle, ObjectId taskId)
    {
        try
        {
            await handle.ReleaseAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release lock for emotion task {TaskId}", taskId);
        }

        try
        {
            await handle.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispose lock for emotion task {TaskId}", taskId);
        }
    }
}
