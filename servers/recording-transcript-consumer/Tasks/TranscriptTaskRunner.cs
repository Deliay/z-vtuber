using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Context;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;
using Z.Vtuber.RecordingTranscript.Consumer.Services;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

/// <summary>
/// 任务暂缓（可重试等待）：音频源尚未就绪（如 live 任务已有进度但录播文件仍在写/未归档）。
/// 不置 FAILED，置回 PENDING 由扫描器稍后重试，避免携空文件名/未就绪文件发起识别。
/// </summary>
public sealed class TranscriptTaskDeferredException(string message) : Exception(message);

/// <summary>
/// 单任务编排：按 Mode 选择音源——
/// 仅 Position==0 的 live 任务接实时流；已有进度的任务一律转 file 从 position 断点续跑（缺口不回溯）；
/// 文件未就绪时暂缓为 PENDING 等待，不失败也不 DONE。
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
    ITranscriptContextBuilder contextBuilder,
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

            // 运行时以最新 recording.Files 重新选取识别文件（开播建任务时文件列表为空，结束事件不会回填既有任务）
            var resolvedFile = ResolveRecordingFile(recording, task);

            long offset;
            var canAttachLive = string.Equals(task.Mode, TranscriptTaskModes.Live, StringComparison.Ordinal)
                && task.Position == 0;
            if (canAttachLive)
            {
                // 从未产出片段：允许接实时流（首发/开播初）。已有进度再接实时流会永久丢失 Position→现在 之间的音频。
                var probe = await recordingClient.ProbeLiveAudioAsync(task.RoomId, ct);
                switch (probe)
                {
                    case LiveAudioProbe.Live:
                        offset = ComputeLiveOffset(recording, task);
                        source = audioFactory.CreateLive(recordingClient.BuildLiveAudioUrl(task.RoomId), offset);
                        break;
                    case LiveAudioProbe.NotLive:
                        // 直播确已结束：转 file 从 position 断点补尾（缺失窗口不回溯）
                        offset = task.Position;
                        source = await CreateFileSourceAsync(resolvedFile, task, ct);
                        break;
                    default:
                        // 传输异常/未知状态不得当作已结束：抛出让任务 FAILED，retry/接管后重试
                        throw new InvalidOperationException(
                            $"live audio probe for room {task.RoomId} was inconclusive; refusing to read a possibly still-recording file");
                }
            }
            else
            {
                // 手动/自动 file 任务，或已有进度的 live 任务重试：只能从录播文件断点续跑。
                // live 已有进度时必须等场次结束（文件写完）再读，否则读到当前 EOF 提前 DONE、后半场永久缺失。
                if (string.Equals(task.Mode, TranscriptTaskModes.Live, StringComparison.Ordinal)
                    && (recording is null || recording.EndedAt is null))
                {
                    throw new TranscriptTaskDeferredException(
                        "live task already has transcript progress; waiting for the recording file to be finalized");
                }

                offset = task.Position;
                source = await CreateFileSourceAsync(resolvedFile, task, ct);
            }

            // 识别上下文按任务启动时的数据快照组装；recording 缺失时降级为空上下文继续识别
            IReadOnlyList<string> context;
            if (recording is not null)
            {
                context = await contextBuilder.BuildAsync(recording, ct);
            }
            else
            {
                logger.LogWarning(
                    "Recording {RecordingId} not found for transcript task {TaskId}; proceeding without context",
                    task.RecordingId, task.Id);
                context = [];
            }

            var asr = asrFactory.Create(model, offset, context);
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
        catch (TranscriptTaskDeferredException ex)
        {
            // 音频源未就绪：置回 PENDING 等扫描器稍后重试（既不 DONE 也不 FAILED）
            logger.LogInformation("Transcript task {TaskId} deferred (retryable): {Reason}", task.Id, ex.Message);
            try
            {
                if (await OwnsLockAsync(handle, task, CancellationToken.None))
                {
                    await tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Pending, null, CancellationToken.None);
                }
            }
            catch (Exception deferEx)
            {
                logger.LogWarning(deferEx, "Failed to defer transcript task {TaskId}", task.Id);
            }
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

    /// <summary>以运行时最新 recording.Files 选取文件（优先 [RAW]），缺失回退任务已存文件名。</summary>
    private static string ResolveRecordingFile(RecordingRecord? recording, TranscriptTaskRecord task)
    {
        var fromRecording = recording is not null
            ? TranscriptEnqueueService.PickFile(recording.Files)
            : string.Empty;
        return !string.IsNullOrEmpty(fromRecording) ? fromRecording : task.File;
    }

    /// <summary>创建 file 音源；文件名解析为空（录播未就绪/仍在写）时暂缓重试，绝不携空文件名发起请求。</summary>
    private async Task<IAudioSource> CreateFileSourceAsync(
        string resolvedFile, TranscriptTaskRecord task, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(resolvedFile))
        {
            throw new TranscriptTaskDeferredException("recording file is not available yet");
        }

        await PersistResolvedFileAsync(task, resolvedFile, ct);
        return audioFactory.CreateFile(recordingClient.BuildFileUrl(task.RoomId, resolvedFile), task.Position);
    }

    private async Task PersistResolvedFileAsync(TranscriptTaskRecord task, string resolvedFile, CancellationToken ct)
    {
        if (string.Equals(task.File, resolvedFile, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await tasks.UpdateFileAsync(task.Id, resolvedFile, ct);
            task.File = resolvedFile;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist resolved file for transcript task {TaskId}", task.Id);
        }
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
