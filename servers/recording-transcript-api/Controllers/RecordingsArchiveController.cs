using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class RecordingsArchiveController(
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptModelRepository models,
    TranscriptAutoEnqueueRepository autoEnqueue) : ControllerBase
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    [HttpGet("api/recordings")]
    public async Task<IActionResult> ListRecordings(
        [FromQuery] long? roomId = null,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] DateTime? before = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
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
            var taskList = await tasks.ListAsync(recording.Id, null, null, MaxLimit, ct);
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

        var mid = await ResolveMidAsync(roomId, ct);
        await autoEnqueue.UpsertAsync(roomId, mid, request.Enable, ct);
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
