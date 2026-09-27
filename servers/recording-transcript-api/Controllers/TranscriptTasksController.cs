using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Contracts;
using Z.Vtuber.RecordingTranscript.Api.Sse;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class TranscriptTasksController(
    IOptions<TranscriptApiOptions> options,
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptSegmentRepository segments,
    TranscriptModelRepository models,
    TranscriptSegmentStream segmentStream,
    ILogger<TranscriptTasksController>? logger = null) : ControllerBase
{
    internal const int DefaultLimit = 50;

    /// <summary>播放偏移合法范围 ±24h（毫秒），与契约 <c>UpdateTranscriptTaskOffsetRequest</c> 一致。</summary>
    internal const long MaxOffsetMs = 86_400_000;

    private readonly ILogger<TranscriptTasksController> _logger =
        logger ?? NullLogger<TranscriptTasksController>.Instance;

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

        if (recording.EndedAt is null)
        {
            return BadRequest(new ErrorResponse("recording has not ended"));
        }

        var model = await models.GetByIdAsync(modelId, ct);
        if (model is null || !model.Enabled)
        {
            return NotFound(new ErrorResponse("recording or model not found"));
        }

        var existing = await tasks.GetByRecordingAndModelAsync(recordingId, modelId, ct);
        if (existing is not null)
        {
            LogEnqueued(LogLevel.Debug, existing, " (idempotent hit, 409)");
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
                LogEnqueued(LogLevel.Debug, duplicate, " (idempotent hit, 409)");
                return Conflict(new EnqueueResponseDto(duplicate.Id.ToString(), duplicate.Status));
            }

            throw;
        }

        LogEnqueued(LogLevel.Information, task, string.Empty);
        return Ok(new EnqueueResponseDto(task.Id.ToString(), task.Status));
    }

    /// <summary>
    /// 入队提交信息日志（Info=建任务成功，Debug=409 幂等命中）：只输出任务元信息，绝不输出模型 ApiKey。
    /// </summary>
    private void LogEnqueued(LogLevel level, TranscriptTaskRecord task, string suffix) =>
        _logger.Log(
            level,
            "transcript task enqueued: taskId={TaskId} recordingId={RecordingId} roomId={RoomId} file={File} modelId={ModelId} mode={Mode} source={Source}{Suffix}",
            task.Id,
            task.RecordingId,
            task.RoomId,
            task.File,
            task.ModelId,
            task.Mode,
            task.Source,
            suffix);

    [HttpGet("api/transcripts/task")]
    public async Task<IActionResult> ListTasks(
        [FromQuery] string? recordingId = null,
        [FromQuery] long? roomId = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, options.Value.MaxPageSize);

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
            segmentList.Select(s => TranscriptMapping.ToSegmentDto(s, task.OffsetMs)).ToList()));
    }

    /// <summary>
    /// 设置任务播放偏移（契约：<c>contracts/recording-transcript.openapi.yaml /api/transcripts/task/{taskId}/offset</c>）。
    /// 仅写入 <c>transcript_tasks.OffsetMs</c>，识别链路与落库片段数据零改动；
    /// 缺失或超出 ±24h 返回 400，taskId 非法或不存在返回 404。
    /// </summary>
    [HttpPut("api/transcripts/task/{taskId}/offset")]
    public async Task<IActionResult> UpdateTaskOffset(
        string taskId,
        [FromBody] UpdateTranscriptTaskOffsetRequest? request,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(taskId, out var id))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        if (request?.OffsetMs is null
            or < -MaxOffsetMs
            or > MaxOffsetMs)
        {
            return BadRequest(new ErrorResponse("offsetMs out of range"));
        }

        var offsetMs = request.OffsetMs.Value;
        if (!await tasks.UpdateOffsetAsync(id, offsetMs, ct))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var task = await tasks.GetByIdAsync(id, ct);
        if (task is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        _logger.LogInformation(
            "transcript task offset updated: taskId={TaskId} offsetMs={OffsetMs}", task.Id, offsetMs);
        return Ok(await MapTaskAsync(task, ct));
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
