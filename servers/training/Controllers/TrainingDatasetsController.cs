using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 训练数据集 API（契约 <c>contracts/training.openapi.yaml /api/training/datasets</c>，接口 1-4）：
/// 列表 / 新建 / 重命名 / 删除（级联清条目），错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/datasets")]
public sealed class TrainingDatasetsController(
    TrainingDatasetRepository datasets,
    TrainingDatasetItemRepository items,
    ILogger<TrainingDatasetsController>? logger = null,
    VectorSyncService? vectors = null,
    SegmentSyncService? segments = null) : ControllerBase
{
    /// <summary>本期唯一合法数据集类型（预留其他模态）。</summary>
    internal const string AudioType = "audio";

    /// <summary>数据集名字最大长度（trim 后），与契约 <c>TrainingDataset.name</c> 一致。</summary>
    internal const int MaxNameLength = 128;

    private readonly ILogger<TrainingDatasetsController> _logger =
        logger ?? NullLogger<TrainingDatasetsController>.Instance;

    /// <summary>查询数据集列表：按 <c>type</c> 过滤、CreatedAt 升序；<c>type</c> 缺失或非 <c>audio</c> 返回 400。</summary>
    [HttpGet]
    public async Task<ActionResult<TrainingDatasetListDto>> List(
        [FromQuery] string? type, CancellationToken ct = default)
    {
        if (type != AudioType)
        {
            return BadRequest(new ErrorResponse("unsupported dataset type"));
        }

        var list = await datasets.ListAsync(type, ct);
        return Ok(new TrainingDatasetListDto(list.Select(TrainingMapping.ToDatasetDto).ToList()));
    }

    /// <summary>新建数据集：trim 后非空且 ≤128 字符，重名返回 409。</summary>
    [HttpPost]
    public async Task<ActionResult<TrainingDatasetDto>> Create(
        [FromBody] TrainingDatasetCreateRequest? request, CancellationToken ct = default)
    {
        if (request?.Type != AudioType)
        {
            return BadRequest(new ErrorResponse("unsupported dataset type"));
        }

        if (ValidateName(request.Name) is { } nameError)
        {
            return BadRequest(nameError);
        }

        var now = DateTime.UtcNow;
        var record = new TrainingDatasetRecord
        {
            Name = request.Name!.Trim(),
            Type = AudioType,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await datasets.InsertAsync(record, ct);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("dataset name exists"));
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("dataset name exists"));
        }

        _logger.LogInformation(
            "training dataset created: datasetId={DatasetId} name={Name}", record.Id, record.Name);
        return StatusCode(StatusCodes.Status201Created, TrainingMapping.ToDatasetDto(record));
    }

    /// <summary>重命名数据集：400 名字非法、404 不存在、409 重名。</summary>
    [HttpPut("{datasetId}")]
    public async Task<ActionResult<TrainingDatasetDto>> Rename(
        string datasetId, [FromBody] TrainingDatasetRenameRequest? request, CancellationToken ct = default)
    {
        if (ValidateName(request?.Name) is { } nameError)
        {
            return BadRequest(nameError);
        }

        if (!ObjectId.TryParse(datasetId, out var id))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        var name = request!.Name!.Trim();
        try
        {
            if (!await datasets.RenameAsync(id, name, ct))
            {
                return NotFound(new ErrorResponse("dataset not found"));
            }
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("dataset name exists"));
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("dataset name exists"));
        }

        _logger.LogInformation(
            "training dataset renamed: datasetId={DatasetId} name={Name}", id, name);

        // 回读落库结果（CreatedAt 等真实时间戳），单读而非全量列举
        var stored = await datasets.GetByIdAsync(id, ct);
        if (stored is null)
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        return Ok(TrainingMapping.ToDatasetDto(stored));
    }

    /// <summary>删除数据集：先级联清条目再删数据集，204 / 404。</summary>
    [HttpDelete("{datasetId}")]
    public async Task<ActionResult> Delete(string datasetId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var id))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        // 级联：先删条目再删数据集（数据集不存在时同样安全幂等）
        await items.DeleteByDatasetAsync(id, ct);
        if (!await datasets.DeleteAsync(id, ct))
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        // 级联清 Milvus 向量（best-effort）。
        if (vectors is not null)
        {
            await vectors.DeleteDatasetAsync(id, ct);
        }

        // 级联清候选短句反向索引（best-effort）。
        if (segments is not null)
        {
            await segments.DeleteDatasetAsync(id, ct);
        }

        _logger.LogInformation("training dataset deleted: datasetId={DatasetId}", id);
        return NoContent();
    }

    /// <summary>名字校验：trim 后为空 → <c>name is required</c>，长度 &gt; 128 → <c>name too long</c>。</summary>
    private static ErrorResponse? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ErrorResponse("name is required");
        }

        return name.Trim().Length > MaxNameLength
            ? new ErrorResponse("name too long")
            : null;
    }

    private static bool IsDuplicateKey(MongoWriteException ex) =>
        ex.WriteError?.Category == ServerErrorCategory.DuplicateKey;

    private static bool IsDuplicateKey(MongoBulkWriteException ex) =>
        ex.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey);
}
