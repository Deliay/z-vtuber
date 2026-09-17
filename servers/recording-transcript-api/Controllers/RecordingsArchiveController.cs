using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class RecordingsArchiveController(
    IOptions<TranscriptApiOptions> options,
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptModelRepository models,
    TranscriptAutoEnqueueRepository autoEnqueue) : ControllerBase
{
    internal const int DefaultLimit = 50;

    [HttpGet("api/recordings")]
    public async Task<IActionResult> ListRecordings(
        [FromQuery] long? roomId = null,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] DateTime? before = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, options.Value.MaxPageSize);
        var items = await recordings.ListAsync(roomId, limit, before, ct);
        return Ok(new { items = items.Select(TranscriptMapping.ToRecordingItem).ToList() });
    }

    [HttpGet("api/recordings/{roomId:long}/transcripts")]
    public async Task<IActionResult> GetRoomTranscripts(long roomId, CancellationToken ct = default)
    {
        var setting = await autoEnqueue.GetByRoomIdAsync(roomId, ct);

        var allModels = await models.ListAsync(ct);
        var modelById = allModels.ToDictionary(m => m.Id);

        var recordingList = await recordings.ListAsync(roomId, DefaultLimit, null, ct);
        var items = new List<RoomTranscriptRecordingDto>();

        foreach (var recording in recordingList)
        {
            var taskList = await tasks.ListAsync(recording.Id, null, null, options.Value.MaxPageSize, ct);
            var taskRefs = taskList.Select(t => new RoomTranscriptTaskRefDto(
                t.Id.ToString(),
                t.ModelId.ToString(),
                modelById.TryGetValue(t.ModelId, out var model) ? model.Name : null,
                t.Status,
                t.Position,
                t.Source,
                t.UpdatedAt)).ToList();

            items.Add(new RoomTranscriptRecordingDto(
                recording.Id.ToString(),
                recording.Title,
                recording.StartedAt,
                recording.EndedAt,
                taskRefs));
        }

        return Ok(new RoomTranscriptViewDto(roomId, setting?.Enabled ?? false, items));
    }

    [HttpPost("api/recordings/{roomId:long}/transcripts/settings")]
    public async Task<IActionResult> UpdateSettings(
        long roomId,
        [FromBody] TranscriptSettingsUpdateRequest? request,
        CancellationToken ct = default)
    {
        if (request is null)
        {
            return BadRequest(new ErrorResponse("request body is required"));
        }

        ObjectId? boundModelId = null;
        if (request.Enable)
        {
            if (string.IsNullOrWhiteSpace(request.ModelId) || !ObjectId.TryParse(request.ModelId, out var parsed))
            {
                return BadRequest(new ErrorResponse("modelId is required and must be a valid id when enable is true"));
            }

            var model = await models.GetByIdAsync(parsed, ct);
            if (model is null)
            {
                return BadRequest(new ErrorResponse("model not found"));
            }

            if (!model.Enabled)
            {
                return BadRequest(new ErrorResponse("model is disabled"));
            }

            boundModelId = parsed;
        }
        else if (!string.IsNullOrWhiteSpace(request.ModelId) && ObjectId.TryParse(request.ModelId, out var parsedBound))
        {
            // enable=false：Plan 口径为「仅更新开关」，此分支允许顺带改绑但不校验模型状态；
            // 再次开启时（enable=true）仍会强制校验 modelId 指向已启用模型。
            boundModelId = parsedBound;
        }

        var mid = await ResolveMidAsync(roomId, ct);
        await autoEnqueue.UpsertAsync(roomId, mid, request.Enable, boundModelId, ct);
        return Ok(new TranscriptSettingsUpdateResponse(roomId, request.Enable));
    }

    private async Task<long> ResolveMidAsync(long roomId, CancellationToken ct)
    {
        var setting = await autoEnqueue.GetByRoomIdAsync(roomId, ct);
        if (setting is { Mid: > 0 })
        {
            return setting.Mid;
        }

        var latest = await recordings.ListAsync(roomId, 1, null, ct);
        if (latest.Count > 0 && latest[0].Mid > 0)
        {
            return latest[0].Mid;
        }

        return 0;
    }
}
