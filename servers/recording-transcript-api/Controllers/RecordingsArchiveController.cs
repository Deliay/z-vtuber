using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Clients;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class RecordingsArchiveController(
    IOptions<TranscriptApiOptions> options,
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptModelRepository models,
    TranscriptAutoEnqueueRepository autoEnqueue,
    AccountBasicInfoRepository accountInfo,
    IRecordingFileStatsClient fileStats,
    ILogger<RecordingsArchiveController> logger) : ControllerBase
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
        var records = await recordings.ListAsync(roomId, limit, before, ct);

        var filtered = await FilterEmptyRecordingsAsync(records, ct);
        var anchorNames = await ResolveAnchorNamesAsync(records.Select(r => r.Mid), ct);

        var items = filtered is null
            ? records.Select(r => TranscriptMapping.ToRecordingItem(
                r, anchorNames.GetValueOrDefault(r.Mid))).ToList()
            : filtered
                .Select(f => TranscriptMapping.ToRecordingItem(
                    f.Record, anchorNames.GetValueOrDefault(f.Record.Mid), f.Files, f.Size))
                .ToList();

        return Ok(new { items });
    }

    [HttpGet("api/recordings/{roomId:long}/transcripts")]
    public async Task<IActionResult> GetRoomTranscripts(long roomId, CancellationToken ct = default)
    {
        var setting = await autoEnqueue.GetByRoomIdAsync(roomId, ct);

        var allModels = await models.ListAsync(ct);
        var modelById = allModels.ToDictionary(m => m.Id);

        var recordingList = await recordings.ListAsync(roomId, DefaultLimit, null, ct);
        var filtered = await FilterEmptyRecordingsAsync(recordingList, ct);
        var visibleRecordings = filtered is null
            ? recordingList
            : filtered.Select(f => f.Record).ToList();

        var items = new List<RoomTranscriptRecordingDto>();

        foreach (var recording in visibleRecordings)
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

    [HttpGet("api/transcripts/candidate-rooms")]
    public async Task<IActionResult> ListCandidateRooms(CancellationToken ct = default)
    {
        var rooms = await recordings.DistinctRoomsAsync(ct);
        var anchorNames = await ResolveAnchorNamesAsync(rooms.Select(r => r.Mid), ct);

        var items = rooms
            .Select(r => new CandidateRoomDto(r.RoomId, r.Mid, anchorNames.GetValueOrDefault(r.Mid)))
            .ToList();

        return Ok(new { items });
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

    [HttpDelete("api/recordings/{roomId:long}/transcripts/settings")]
    public async Task<IActionResult> DeleteSettings(long roomId, CancellationToken ct = default)
    {
        var deleted = await autoEnqueue.DeleteByRoomIdAsync(roomId, ct);
        return deleted
            ? NoContent()
            : NotFound(new ErrorResponse("transcript settings not found"));
    }

    /// <summary>
    /// 以 recording 磁盘数据过滤「所有文件均不存在」的空场次。
    /// 返回 null 表示 recording 不可达已降级（调用方应返回未过滤原始列表）。
    /// </summary>
    private async Task<IReadOnlyList<FilteredRecording>?> FilterEmptyRecordingsAsync(
        IReadOnlyList<RecordingRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var queries = records
            .Where(r => r.Files.Count > 0)
            .Select(r => new RecordingFileStatsQuery(r.RoomId, r.Files))
            .ToList();
        if (queries.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<(long RoomId, string FileName), RecordingFileStat> stats;
        try
        {
            stats = await fileStats.GetFileStatsAsync(queries, ct);
        }
        catch (Exception ex) when (
            !ct.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex,
                "Failed to query recording file stats; returning unfiltered recordings for {Count} rooms",
                queries.Count);
            return null;
        }

        var filtered = new List<FilteredRecording>();
        foreach (var record in records)
        {
            var existingFiles = record.Files
                .Where(f => stats.TryGetValue((record.RoomId, f), out var stat) && stat.Exists)
                .ToList();
            if (existingFiles.Count == 0)
            {
                continue;
            }

            var size = existingFiles.Sum(f => stats[(record.RoomId, f)].Size ?? 0);
            filtered.Add(new FilteredRecording(record, existingFiles, size));
        }

        return filtered;
    }

    private async Task<Dictionary<long, string?>> ResolveAnchorNamesAsync(
        IEnumerable<long> mids, CancellationToken ct)
    {
        var result = new Dictionary<long, string?>();
        var distinct = mids.Where(m => m > 0).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return result;
        }

        var infos = await accountInfo.GetLatestByMidsAsync(distinct, ct);
        foreach (var mid in distinct)
        {
            result[mid] = infos.TryGetValue(mid, out var info)
                ? TranscriptMapping.ExtractAnchorName(info.Data)
                : null;
        }

        return result;
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

    private sealed record FilteredRecording(
        RecordingRecord Record, IReadOnlyList<string> Files, long Size);
}
