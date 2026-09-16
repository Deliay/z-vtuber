using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Contracts;
using Z.Vtuber.RecordingTranscript.Api.Sse;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class TranscriptTasksController(
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptSegmentRepository segments,
    TranscriptModelRepository models,
    TranscriptSegmentStream segmentStream) : ControllerBase
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    [HttpPost("api/transcripts/enqueue")]
    public async Task<IActionResult> Enqueue([FromBody] EnqueueRequest? request, CancellationToken ct = default)
    {
        if (request is null
            || !ObjectId.TryParse(request.RecordingId, out var recordingId)
            || !ObjectId.TryParse(request.ModelId, out var modelId))
        {
            return NotFound(new ErrorResponse("recording or model not found"));
        }

        var recording = await recordings.GetByIdAsync(recordingId, ct);
        if (recording is null)
        {
            return NotFound(new ErrorResponse("recording or model not found"));
        }

        var model = await models.GetByIdAsync(modelId, ct);
        if (model is null || !model.Enabled)
        {
            return NotFound(new ErrorResponse("recording or model not found"));
        }

        var existing = await tasks.GetByRecordingAndModelAsync(recordingId, modelId, ct);
        if (existing is not null)
        {
            return Conflict(new EnqueueResponseDto(existing.Id.ToString(), existing.Status));
        }

        var now = DateTime.UtcNow;
        var task = new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = recording.RoomId,
            File = PickFile(recording.Files),
            ModelId = modelId,
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Manual,
            Status = TranscriptTaskStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await tasks.InsertAsync(task, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var duplicate = await tasks.GetByRecordingAndModelAsync(recordingId, modelId, ct);
            if (duplicate is not null)
            {
                return Conflict(new EnqueueResponseDto(duplicate.Id.ToString(), duplicate.Status));
            }

            throw;
        }

        return Ok(new EnqueueResponseDto(task.Id.ToString(), task.Status));
    }

    [HttpGet("api/transcripts/task")]
    public async Task<IActionResult> ListTasks(
        [FromQuery] string? recordingId = null,
        [FromQuery] long? roomId = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        ObjectId? recordingObjectId = null;
        if (!string.IsNullOrWhiteSpace(recordingId))
        {
            if (!ObjectId.TryParse(recordingId, out var parsed))
            {
                return Ok(new { items = Array.Empty<TranscriptTaskDto>() });
            }

            recordingObjectId = parsed;
        }

        var list = await tasks.ListAsync(recordingObjectId, roomId, status, limit, ct);
        var dtos = await MapTasksAsync(list, ct);
        return Ok(new { items = dtos });
    }

    [HttpGet("api/transcripts/task/{taskId}")]
    public async Task<IActionResult> GetTask(string taskId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(taskId, out var id))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var task = await tasks.GetByIdAsync(id, ct);
        if (task is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var segmentList = await segments.ListByTaskAsync(id, ct);
        var dto = await MapTaskAsync(task, ct);
        return Ok(new TranscriptTaskDetailDto(
            dto,
            segmentList.Select(TranscriptMapping.ToSegmentDto).ToList()));
    }

    [HttpPost("api/transcripts/task/{taskId}/retry")]
    public async Task<IActionResult> RetryTask(string taskId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(taskId, out var id))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var task = await tasks.GetByIdAsync(id, ct);
        if (task is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        if (task.Status != TranscriptTaskStatus.Failed)
        {
            return Conflict(new ErrorResponse("task is not retryable"));
        }

        await tasks.SetStatusAsync(id, TranscriptTaskStatus.Running, null, ct);
        var updated = await tasks.GetByIdAsync(id, ct) ?? task;
        return Ok(await MapTaskAsync(updated, ct));
    }

    [HttpGet("api/transcripts/task/{taskId}/sse")]
    public async Task<IActionResult> StreamTask(string taskId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(taskId, out var id))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var task = await tasks.GetByIdAsync(id, ct);
        if (task is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        await segmentStream.StreamAsync(Response, id, ct);
        return new EmptyResult();
    }

    internal static string PickFile(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        return files.FirstOrDefault(f => f.Contains("[RAW]", StringComparison.OrdinalIgnoreCase)) ?? files[0];
    }

    private async Task<List<TranscriptTaskDto>> MapTasksAsync(
        IReadOnlyList<TranscriptTaskRecord> list, CancellationToken ct = default)
    {
        var modelById = (await models.ListAsync(ct)).ToDictionary(m => m.Id);
        var recordingCache = new Dictionary<ObjectId, RecordingRecord?>();

        var result = new List<TranscriptTaskDto>(list.Count);
        foreach (var task in list)
        {
            if (!recordingCache.TryGetValue(task.RecordingId, out var recording))
            {
                recording = await recordings.GetByIdAsync(task.RecordingId, ct);
                recordingCache[task.RecordingId] = recording;
            }

            result.Add(TranscriptMapping.ToTaskDto(
                task,
                recording,
                modelById.GetValueOrDefault(task.ModelId)));
        }

        return result;
    }

    private async Task<TranscriptTaskDto> MapTaskAsync(TranscriptTaskRecord task, CancellationToken ct = default)
    {
        var recording = await recordings.GetByIdAsync(task.RecordingId, ct);
        var model = await models.GetByIdAsync(task.ModelId, ct);
        return TranscriptMapping.ToTaskDto(task, recording, model);
    }
}
