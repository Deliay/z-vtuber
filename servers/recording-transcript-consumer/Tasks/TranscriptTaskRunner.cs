using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Services;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

/// <summary>
/// 单任务编排：按 Mode 选择音源（live 探测 200 接流 / 404 转 file 从 position seek 补尾），
/// ffmpeg stdout → ASR 流式识别 → 顺序落库 segment + position（唯一索引幂等）→ Redis pub；
/// 每轮 fencing 校验，失锁即终止；EOF → DONE，异常 → FAILED；finally 释放锁。
/// </summary>
public interface ITranscriptTaskRunner
{
    Task RunAsync(TranscriptTaskRecord task, ITranscriptTaskLock? handle = null, CancellationToken ct = default);
}

public sealed class TranscriptTaskRunner(
    TranscriptTaskRepository tasks,
    TranscriptModelRepository models,
    RecordingRepository recordings,
    IRecordingServiceClient recordingClient,
    IAudioSourceFactory audioFactory,
    IStreamingAsrServiceFactory asrFactory,
    ITranscriptSegmentPersister persister,
    ITranscriptSegmentPublisher publisher,
    ILogger<TranscriptTaskRunner> logger) : ITranscriptTaskRunner
{
    public async Task RunAsync(
        TranscriptTaskRecord task, ITranscriptTaskLock? handle = null, CancellationToken ct = default)
    {
        handle?.StartRenewal(ct);
        IAudioSource? source = null;
        try
        {
            await tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Running, null, ct);

            var model = await models.GetByIdAsync(task.ModelId, ct)
                ?? throw new InvalidOperationException($"Transcript model {task.ModelId} not found");
            var recording = await recordings.GetByIdAsync(task.RecordingId, ct);

            long offset = task.Position;
            var useFile = true;
            if (string.Equals(task.Mode, TranscriptTaskModes.Live, StringComparison.Ordinal))
            {
                var probe = await recordingClient.ProbeLiveAudioAsync(task.RoomId, ct);
                switch (probe)
                {
                    case LiveAudioProbe.Live:
                        offset = ComputeLiveOffset(recording, task);
                        source = audioFactory.CreateLive(recordingClient.BuildLiveAudioUrl(task.RoomId), offset);
                        useFile = false;
                        break;
                    case LiveAudioProbe.NotLive:
                        // 直播确已结束：转 file 模式从 position 断点补尾（缺失窗口不回溯）
                        break;
                    default:
                        // 传输异常/未知状态不得当作已结束：抛出让任务 FAILED，retry/接管后重试
                        throw new InvalidOperationException(
                            $"live audio probe for room {task.RoomId} was inconclusive; refusing to read a possibly still-recording file");
                }
            }

            if (useFile)
            {
                offset = task.Position;
                source = audioFactory.CreateFile(recordingClient.BuildFileUrl(task.RoomId, task.File), offset);
            }

            var asr = asrFactory.Create(offset, model.ApiKey);
            await foreach (var segment in asr.TranscribeAsync(source.Audio, ct).WithCancellation(ct))
            {
                if (!await OwnsLockAsync(handle, task, ct))
                {
                    return;
                }

                var persisted = await persister.PersistAsync(
                    task.Id, segment.Start, segment.End, segment.Text, ct);
                if (persisted)
                {
                    task.Position = segment.End;
                    await publisher.PublishAsync(task.Id, segment, ct);
                }
            }

            // 区分「音源正常结束（EOF, exit 0）」与「音源失败」：
            // ffmpeg 非 0 退出（文件缺失/不可读、直播中途断连）必须 FAILED 可重试，不得静默 DONE。
            var exitCode = await source.WaitForExitAsync(ct);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"audio source exited with code {exitCode}");
            }

            await WriteTerminalAsync(handle, task, TranscriptTaskStatus.Done, null, ct);
            logger.LogInformation("Transcript task {TaskId} completed", task.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Transcript task {TaskId} cancelled", task.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcript task {TaskId} failed", task.Id);
            try
            {
                await WriteTerminalAsync(handle, task, TranscriptTaskStatus.Failed, ex.Message, CancellationToken.None);
            }
            catch (Exception statusEx)
            {
                logger.LogWarning(statusEx, "Failed to mark transcript task {TaskId} as FAILED", task.Id);
            }
        }
        finally
        {
            if (source is not null)
            {
                try
                {
                    await source.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to dispose audio source for transcript task {TaskId}", task.Id);
                }
            }

            if (handle is not null)
            {
                // 释放与销毁各自独立 try，避免释放异常跳过 Dispose 形成僵尸锁
                try
                {
                    await handle.ReleaseAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release transcript task lock for {TaskId}", task.Id);
                }

                try
                {
                    await handle.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose transcript task lock for {TaskId}", task.Id);
                }
            }
        }
    }

    /// <summary>fencing：锁已易主/Redis 抖动时返回 false，调用方须停止写库（保留 RUNNING 交新持有者接管）。</summary>
    private async Task<bool> OwnsLockAsync(
        ITranscriptTaskLock? handle, TranscriptTaskRecord task, CancellationToken ct)
    {
        if (handle is null)
        {
            return true;
        }

        if (await handle.CheckOwnershipAsync(ct))
        {
            return true;
        }

        logger.LogWarning(
            "Transcript task {TaskId} lost lock; leaving status RUNNING for another node to take over", task.Id);
        return false;
    }

    /// <summary>写终态前必须 fencing：失锁则不得写 DONE/FAILED，避免把新持有者正在处理的任务钉死。</summary>
    private async Task WriteTerminalAsync(
        ITranscriptTaskLock? handle,
        TranscriptTaskRecord task,
        string status,
        string? error,
        CancellationToken ct)
    {
        if (!await OwnsLockAsync(handle, task, ct))
        {
            return;
        }

        await tasks.SetStatusAsync(task.Id, status, error, ct);
    }

    private static long ComputeLiveOffset(RecordingRecord? recording, TranscriptTaskRecord task)
    {
        if (recording is null || recording.StartedAt == default)
        {
            return task.Position;
        }

        var elapsed = (long)(DateTime.UtcNow - recording.StartedAt).TotalMilliseconds;
        return Math.Max(elapsed, task.Position);
    }
}
