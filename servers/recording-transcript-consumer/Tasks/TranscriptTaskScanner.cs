using System.Collections.Concurrent;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

/// <summary>
/// 周期扫描：PENDING 或 RUNNING 但锁不存在（上一节点死亡）的任务按 _id 升序抢锁接管；
/// 受模型 MaxConcurrency 门控；抢锁成功置 RUNNING 后交 Runner 执行。
/// </summary>
public sealed class TranscriptTaskScanner(
    TranscriptTaskRepository tasks,
    TranscriptModelRepository models,
    ITranscriptTaskLockProvider lockProvider,
    ITranscriptTaskRunner runner,
    TranscriptConsumerOptions options,
    ILogger<TranscriptTaskScanner> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ObjectId, int> _inFlight = new();
    private readonly object _concurrencyLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                logger.LogWarning(ex, "Transcript task scan iteration failed; continuing.");
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
        var scannable = await tasks.ListScannableAsync(options.ScanBatchSize, ct);
        foreach (var task in scannable)
        {
            ct.ThrowIfCancellationRequested();

            if (string.Equals(task.Status, TranscriptTaskStatus.Running, StringComparison.Ordinal)
                && await lockProvider.IsHeldAsync(task.Id, ct))
            {
                // 他节点正在处理该 RUNNING 任务
                continue;
            }

            var model = await models.GetByIdAsync(task.ModelId, ct);
            if (model is null)
            {
                await tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Failed, "transcript model not found", ct);
                continue;
            }

            if (!await TryReserveConcurrencyAsync(model, ct))
            {
                continue;
            }

            var handle = await lockProvider.TryAcquireAsync(task.Id, ct);
            if (handle is null)
            {
                ReleaseConcurrency(model.Id);
                continue;
            }

            await tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Running, null, ct);
            _ = RunDetachedAsync(task, handle, model.Id, ct);
        }
    }

    private async Task RunDetachedAsync(
        TranscriptTaskRecord task, ITranscriptTaskLock handle, ObjectId modelId, CancellationToken ct)
    {
        try
        {
            await runner.RunAsync(task, handle, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcript task runner for {TaskId} threw", task.Id);
        }
        finally
        {
            ReleaseConcurrency(modelId);
        }
    }

    private async Task<bool> TryReserveConcurrencyAsync(TranscriptModelRecord model, CancellationToken ct)
    {
        var running = await tasks.CountByModelAndStatusAsync(model.Id, TranscriptTaskStatus.Running, ct);
        lock (_concurrencyLock)
        {
            var inFlight = _inFlight.GetValueOrDefault(model.Id);
            if (running + inFlight >= Math.Max(1, model.MaxConcurrency))
            {
                return false;
            }

            _inFlight[model.Id] = inFlight + 1;
            return true;
        }
    }

    private void ReleaseConcurrency(ObjectId modelId)
    {
        lock (_concurrencyLock)
        {
            if (_inFlight.TryGetValue(modelId, out var current) && current > 0)
            {
                _inFlight[modelId] = current - 1;
            }
        }
    }
}
