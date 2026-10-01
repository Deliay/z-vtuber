using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 数据集候选短句反向索引 API（契约 <c>contracts/training.openapi.yaml</c>）：
/// <c>POST segments/rebuild</c> 清空该数据集候选短句并全量重建（与 <c>vectors/rebuild</c> 同构）。
/// 错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/datasets/{datasetId}")]
public sealed class TrainingSegmentsController(
    TrainingDatasetRepository datasets,
    SegmentSyncService segments,
    ILogger<TrainingSegmentsController>? logger = null) : ControllerBase
{
    private readonly ILogger<TrainingSegmentsController> _logger =
        logger ?? NullLogger<TrainingSegmentsController>.Instance;

    /// <summary>
    /// 候选短句反向索引重建：dataset（404）校验 → <see cref="SegmentSyncService.RebuildAsync"/> →
    /// 200 <c>{"rebuilt": n}</c>。
    /// </summary>
    [HttpPost("segments/rebuild")]
    public async Task<ActionResult<TrainingSegmentsRebuildResultDto>> Rebuild(
        string datasetId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var datasetObjectId)
            || await datasets.GetByIdAsync(datasetObjectId, ct) is null)
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        var rebuilt = await segments.RebuildAsync(datasetObjectId, ct);
        _logger.LogInformation(
            "training segments rebuild: datasetId={DatasetId} rebuilt={Rebuilt}", datasetObjectId, rebuilt);
        return Ok(new TrainingSegmentsRebuildResultDto(rebuilt));
    }
}
