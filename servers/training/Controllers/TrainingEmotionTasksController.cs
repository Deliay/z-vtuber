using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 情绪识别任务队列 API（契约 <c>contracts/training.openapi.yaml</c>，接口 13-14）：
/// 队列查询（<c>datasetId?</c>/<c>status?</c>/<c>limit?</c> 过滤 + 分组计数）、失败任务重试。
/// 错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/emotion-tasks")]
public sealed class TrainingEmotionTasksController(
    TrainingEmotionTaskRepository tasks,
    ILogger<TrainingEmotionTasksController>? logger = null) : ControllerBase
{
    /// <summary>limit 缺省。</summary>
    internal const int DefaultLimit = 100;

    /// <summary>limit 上限。</summary>
    internal const int MaxLimit = 500;

    private readonly ILogger<TrainingEmotionTasksController> _logger =
        logger ?? NullLogger<TrainingEmotionTasksController>.Instance;

    /// <summary>
    /// 队列查询：<c>status</c> 归一化大写后过滤、<c>limit</c> 缺省 100 且钳 1~500（非法 → 100）、
    /// CreatedAt 降序；<c>counts</c> 按 <c>datasetId</c> 过滤的分组计数（不受 <c>status</c> 影响）。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<TrainingEmotionTaskListDto>> List(
        [FromQuery] string? datasetId, [FromQuery] string? status, [FromQuery] int? limit,
        CancellationToken ct = default)
    {
        ObjectId? datasetFilter = ObjectId.TryParse(datasetId, out var parsed) ? parsed : null;
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        var effectiveLimit = limit is { } value && value > 0 ? Math.Min(MaxLimit, value) : DefaultLimit;

        var list = await tasks.ListAsync(datasetFilter, statusFilter, effectiveLimit, ct);
        var counts = await tasks.CountByStatusAsync(datasetFilter, ct);

        return Ok(new TrainingEmotionTaskListDto(
            list.Select(TrainingMapping.ToEmotionTaskDto).ToList(),
            TrainingMapping.ToEmotionCountsDto(counts)));
    }

    /// <summary>重试失败任务：仅 FAILED → PENDING（<c>attempts</c> 保留、<c>error</c> 清空）；非 FAILED 400，不存在 404。</summary>
    [HttpPost("{taskId}/retry")]
    public async Task<ActionResult<TrainingEmotionTaskDto>> Retry(string taskId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(taskId, out var id)
            || await tasks.GetByIdAsync(id, ct) is not { } current)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        if (!string.Equals(current.Status, TrainingEmotionTaskStatus.Failed, StringComparison.Ordinal))
        {
            return BadRequest(new ErrorResponse("task not failed"));
        }

        var update = Builders<TrainingEmotionTaskRecord>.Update
            .Set(t => t.Status, TrainingEmotionTaskStatus.Pending)
            .Set(t => t.Error, (string?)null)
            .Set(t => t.StartedAt, (DateTime?)null)
            .Set(t => t.FinishedAt, (DateTime?)null);
        var updated = await tasks.UpdateAsync(id, update, ct);
        if (updated is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        _logger.LogInformation("training emotion task retried: taskId={TaskId}", id);
        return Ok(TrainingMapping.ToEmotionTaskDto(updated));
    }
}
