using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Services;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

/// <summary>
/// 单任务编排：按 Mode 选择音源（live 探测 200 接流 / 404 转 file 从 position seek 补尾），
/// ffmpeg stdout → ASR 流式识别 → 事务落库 segment + position → Redis pub；
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

            long offset;
            if (string.Equals(task.Mode, TranscriptTaskModes.Live, StringComparison.Ordinal))
            {
                var liveAvailable = await recordingClient.IsLiveStreamAvailableAsync(task.RoomId, ct);
                if (liveAvailable)
                {
                    offset = ComputeLiveOffset(recording, task);
                    source = audioFactory.CreateLive(recordingClient.BuildLiveAudioUrl(task.RoomId), offset);
                }
                else
                {
                    offset = task.Position;
                    source = audioFactory.CreateFile(
                        recordingClient.BuildFileUrl(task.RoomId, task.File), offset);
                }
            }
            else
            {
                offset = task.Position;
                source = audioFactory.CreateFile(recordingClient.BuildFileUrl(task.RoomId, task.File), offset);
            }

            var asr = asrFactory.Create(offset, model.ApiKey);
            await foreach (var segment in asr.TranscribeAsync(source.Audio, ct).WithCancellation(ct))
            {
                if (handle is not null && !await handle.CheckOwnershipAsync(ct))
                {
                    logger.LogWarning("Transcript task {TaskId} lost lock; stopping before persisting", task.Id);
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

            await tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Done, null, ct);
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
                await tasks.SetStatusAsync(task.Id, TranscriptTaskStatus.Failed, ex.Message, CancellationToken.None);
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
                await source.DisposeAsync();
            }

            if (handle is not null)
            {
                await handle.ReleaseAsync();
                await handle.DisposeAsync();
            }
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
