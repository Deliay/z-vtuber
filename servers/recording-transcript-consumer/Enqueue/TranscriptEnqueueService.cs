using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Consumer.Enqueue;

public enum EnqueueError
{
    None,
    RecordingNotFound,
    ModelNotFound,
    ModelDisabled,
}

public sealed record EnqueueResult(ObjectId? TaskId, EnqueueError Error);

/// <summary>
/// 幂等建任务：同「录播 + 模型」唯一（唯一索引兜底）；File 优先 RAW，缺失回退首个。
/// 供开播/结束事件订阅、对账补建与手动 enqueue 共用。
/// </summary>
public sealed class TranscriptEnqueueService(
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptModelRepository models,
    TranscriptAutoEnqueueRepository autoEnqueue,
    ILogger<TranscriptEnqueueService> logger)
{
    /// <summary>开播自动建 mode=live 任务；未启用自动识别、无默认模型或非启用后场次时返回 null。</summary>
    public async Task<ObjectId?> EnqueueForStartedAsync(RecordingStartedEvent evt, CancellationToken ct = default)
    {
        var setting = await autoEnqueue.GetByRoomIdAsync(evt.RoomId, ct);
        if (setting is null || !setting.Enabled)
        {
            return null;
        }

        var model = await models.GetDefaultAsync(ct);
        if (model is null || !model.Enabled)
        {
            return null;
        }

        if (!ObjectId.TryParse(evt.RecordingId, out var recordingId))
        {
            logger.LogWarning("Invalid recordingId in recording-started event: {RecordingId}", evt.RecordingId);
            return null;
        }

        var recording = await recordings.GetByIdAsync(recordingId, ct);
        if (recording is null)
        {
            recording = new RecordingRecord
            {
                Id = recordingId,
                RoomId = evt.RoomId,
                Mid = evt.Mid,
                Title = evt.Title,
                Files = [],
                StartedAt = evt.StartedAt,
                CreatedAt = DateTime.UtcNow,
            };
            await recordings.InsertAsync(recording, ct);
        }

        return await EnsureTaskAsync(recording, model, TranscriptTaskModes.Live, TranscriptTaskSources.Auto, ct);
    }

    /// <summary>结束事件补建 mode=file 任务（仅启用后结束的场次）；已存在同录播+模型任务时返回既有任务。</summary>
    public async Task<ObjectId?> EnqueueForFinishedAsync(RecordingFinishedEvent evt, CancellationToken ct = default)
    {
        var setting = await autoEnqueue.GetByRoomIdAsync(evt.RoomId, ct);
        if (setting is null || !setting.Enabled)
        {
            return null;
        }

        var model = await models.GetDefaultAsync(ct);
        if (model is null || !model.Enabled)
        {
            return null;
        }

        if (!ObjectId.TryParse(evt.RecordingId, out var recordingId))
        {
            return null;
        }

        var recording = await recordings.GetByIdAsync(recordingId, ct);
        if (recording is null || recording.EndedAt is null)
        {
            return null;
        }

        if (recording.EndedAt < setting.UpdatedAt)
        {
            // 仅补「启用之后」结束的场次，历史录播不补
            return null;
        }

        return await EnsureTaskAsync(recording, model, TranscriptTaskModes.File, TranscriptTaskSources.Auto, ct);
    }

    public Task<ObjectId> EnsureFileTaskAsync(
        RecordingRecord recording, TranscriptModelRecord model, string source, CancellationToken ct = default) =>
        EnsureTaskAsync(recording, model, TranscriptTaskModes.File, source, ct);

    public Task<ObjectId> EnsureLiveTaskAsync(
        RecordingRecord recording, TranscriptModelRecord model, string source, CancellationToken ct = default) =>
        EnsureTaskAsync(recording, model, TranscriptTaskModes.Live, source, ct);

    public async Task<EnqueueResult> EnqueueManualAsync(
        ObjectId recordingId, ObjectId modelId, CancellationToken ct = default)
    {
        var recording = await recordings.GetByIdAsync(recordingId, ct);
        if (recording is null)
        {
            return new EnqueueResult(null, EnqueueError.RecordingNotFound);
        }

        var model = await models.GetByIdAsync(modelId, ct);
        if (model is null)
        {
            return new EnqueueResult(null, EnqueueError.ModelNotFound);
        }

        if (!model.Enabled)
        {
            return new EnqueueResult(null, EnqueueError.ModelDisabled);
        }

        var taskId = await EnsureTaskAsync(
            recording, model, TranscriptTaskModes.File, TranscriptTaskSources.Manual, ct);
        return new EnqueueResult(taskId, EnqueueError.None);
    }

    private async Task<ObjectId> EnsureTaskAsync(
        RecordingRecord recording,
        TranscriptModelRecord model,
        string mode,
        string source,
        CancellationToken ct)
    {
        var existing = await tasks.GetByRecordingAndModelAsync(recording.Id, model.Id, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = DateTime.UtcNow;
        var record = new TranscriptTaskRecord
        {
            RecordingId = recording.Id,
            RoomId = recording.RoomId,
            File = PickFile(recording.Files),
            ModelId = model.Id,
            Type = TranscriptTaskTypes.Recording,
            Mode = mode,
            Source = source,
            Position = 0,
            Status = TranscriptTaskStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            return await tasks.InsertAsync(record, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var winner = await tasks.GetByRecordingAndModelAsync(recording.Id, model.Id, ct);
            return winner?.Id ?? record.Id;
        }
    }

    /// <summary>选取识别文件：优先含 [RAW] 的文件，缺失回退首个；无文件返回空串。</summary>
    public static string PickFile(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        foreach (var file in files)
        {
            if (file.Contains("[RAW]", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return files[0];
    }
}
