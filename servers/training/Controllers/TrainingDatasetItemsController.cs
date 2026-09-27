using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 训练数据集条目 API（契约 <c>contracts/training.openapi.yaml</c>，接口 5-8）：
/// 列表（过滤/排序）、从识别结果导入、四字段更新、批量删除。
/// </summary>
[ApiController]
[Route("api/training/datasets/{datasetId}/items")]
public sealed class TrainingDatasetItemsController(
    TrainingDatasetRepository datasets,
    TrainingDatasetItemRepository items,
    ITranscriptApiClient transcript,
    ILogger<TrainingDatasetItemsController>? logger = null) : ControllerBase
{
    private readonly ILogger<TrainingDatasetItemsController> _logger =
        logger ?? NullLogger<TrainingDatasetItemsController>.Instance;

    /// <summary>条目列表：按 <c>audioType</c>/<c>source</c> 过滤，CreatedAt、AudioKey 升序，本期全量。</summary>
    [HttpGet]
    public async Task<ActionResult<TrainingDatasetItemListDto>> List(
        string datasetId, [FromQuery] string? audioType, [FromQuery] string? source,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var id) || !await DatasetExistsAsync(id, ct))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        var list = await items.ListAsync(id, audioType, source, ct);
        return Ok(new TrainingDatasetItemListDto(list.Select(TrainingMapping.ToItemDto).ToList()));
    }

    /// <summary>
    /// 从识别结果导入：数据集 404 → taskId 非法/任务不存在 404 → 非 DONE 400 <c>task not done</c> →
    /// 按 <see cref="TrainingMapping.ToImportItem"/> 映射 → 无序批量去重写入 → 200 <c>{imported, skipped}</c>；
    /// transcript-api 不可达/超时 → 502 <c>transcript service unavailable</c>。
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<TrainingDatasetImportResultDto>> Import(
        string datasetId, [FromBody] TrainingDatasetImportRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var id) || !await DatasetExistsAsync(id, ct))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        var taskId = request?.TaskId;
        if (string.IsNullOrWhiteSpace(taskId) || !ObjectId.TryParse(taskId, out _))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        TranscriptTaskDetail? detail;
        try
        {
            detail = await transcript.GetTaskDetailAsync(taskId, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "training dataset import failed: transcript service unreachable");
            return TranscriptUnavailable();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TranscriptUnavailable();
        }

        if (detail is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        if (detail.Task.Status != TranscriptTaskStatus.Done)
        {
            return BadRequest(new ErrorResponse("task not done"));
        }

        var now = DateTime.UtcNow;
        var records = detail.Segments
            .Select(segment => TrainingMapping.ToImportItem(
                id, taskId, detail.Task.RoomId, detail.Task.File, detail.Task.OffsetMs,
                segment.Start, segment.End, segment.Text, now))
            .ToList();
        var (imported, skipped) = await items.InsertManyIgnoreDuplicatesAsync(records, ct);

        _logger.LogInformation(
            "training dataset import: datasetId={DatasetId} taskId={TaskId} imported={Imported} skipped={Skipped}",
            datasetId, taskId, imported, skipped);
        return Ok(new TrainingDatasetImportResultDto(imported, skipped));
    }

    /// <summary>
    /// 更新条目四字段：请求中缺失的字段保持不变；<c>endMs</c> 显式 null 置空；
    /// <c>audioUri</c>/<c>text</c>/<c>startMs</c> 显式 null → 400；时间非法 → 400 <c>invalid time range</c>。
    /// </summary>
    [HttpPut("{itemId}")]
    public async Task<ActionResult<TrainingDatasetItemDto>> Update(
        string datasetId, string itemId, [FromBody] TrainingDatasetItemUpdateRequest? request,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var id) || !await DatasetExistsAsync(id, ct))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        if (!ObjectId.TryParse(itemId, out var itemObjectId))
        {
            return NotFound(new ErrorResponse("item not found"));
        }

        var current = await items.GetByIdAsync(id, itemObjectId, ct);
        if (current is null)
        {
            return NotFound(new ErrorResponse("item not found"));
        }

        var audioUriKind = request?.AudioUri.ValueKind ?? JsonValueKind.Undefined;
        var textKind = request?.Text.ValueKind ?? JsonValueKind.Undefined;
        var startKind = request?.StartMs.ValueKind ?? JsonValueKind.Undefined;
        var endKind = request?.EndMs.ValueKind ?? JsonValueKind.Undefined;

        // audioUri/text/startMs 显式 null → 400（类型不符同口径兜底）
        if (audioUriKind is not (JsonValueKind.Undefined or JsonValueKind.String)
            || textKind is not (JsonValueKind.Undefined or JsonValueKind.String)
            || startKind is JsonValueKind.Null)
        {
            return BadRequest(new ErrorResponse("field cannot be null"));
        }

        if (startKind is not (JsonValueKind.Undefined or JsonValueKind.Number)
            || endKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Number))
        {
            return BadRequest(new ErrorResponse("invalid time range"));
        }

        // 缺失字段取现值；endMs 显式 null → 置空
        var startMs = startKind == JsonValueKind.Number ? request!.StartMs.GetInt64() : current.StartMs;
        long? endMs = endKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => request!.EndMs.GetInt64(),
            _ => current.EndMs,
        };
        if (!TrainingMapping.IsValidTimeRange(startMs, endMs))
        {
            return BadRequest(new ErrorResponse("invalid time range"));
        }

        // 仓储不隐式刷新 UpdatedAt，由控制器显式刷新（同时作为 UpdateDefinition 的起始项）
        var update = Builders<TrainingDatasetItemRecord>.Update
            .Set(i => i.UpdatedAt, DateTime.UtcNow);
        if (audioUriKind == JsonValueKind.String)
        {
            update = update.Set(i => i.AudioUri, request!.AudioUri.GetString() ?? string.Empty);
        }

        if (textKind == JsonValueKind.String)
        {
            update = update.Set(i => i.Text, request!.Text.GetString() ?? string.Empty);
        }

        if (startKind == JsonValueKind.Number)
        {
            update = update.Set(i => i.StartMs, startMs);
        }

        if (endKind is JsonValueKind.Number or JsonValueKind.Null)
        {
            update = update.Set(i => i.EndMs, endMs);
        }

        var updated = await items.UpdateAsync(id, itemObjectId, update, ct);
        if (updated is null)
        {
            return NotFound(new ErrorResponse("item not found"));
        }

        return Ok(TrainingMapping.ToItemDto(updated));
    }

    /// <summary>批量删除：数据集 404；非法条目 id 跳过，返回实际删除数。</summary>
    [HttpPost("batch-delete")]
    public async Task<ActionResult<TrainingDatasetBatchDeleteResultDto>> BatchDelete(
        string datasetId, [FromBody] TrainingDatasetBatchDeleteRequest? request,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var id) || !await DatasetExistsAsync(id, ct))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        var itemIds = new List<ObjectId>();
        foreach (var raw in request?.ItemIds ?? [])
        {
            if (ObjectId.TryParse(raw, out var parsed))
            {
                itemIds.Add(parsed);
            }
        }

        var deleted = itemIds.Count == 0
            ? 0
            : await items.DeleteManyAsync(id, itemIds, ct);
        return Ok(new TrainingDatasetBatchDeleteResultDto(deleted));
    }

    /// <summary>数据集是否存在（按 id 单读，不全量列举）。</summary>
    private async Task<bool> DatasetExistsAsync(ObjectId id, CancellationToken ct) =>
        await datasets.GetByIdAsync(id, ct) is not null;

    private ObjectResult TranscriptUnavailable() =>
        StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("transcript service unavailable"));
}
