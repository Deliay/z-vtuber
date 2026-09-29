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
/// 模型（Model）API（契约 <c>contracts/training.openapi.yaml</c>，接口 7-10）：
/// 列表（<c>enabled?</c> 过滤，含服务端拼好的 <c>slug</c>）/ 手动添加 / 更新（改名/启停）/ 删除。
/// 错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/models")]
public sealed class TrainingModelsController(
    TrainingModelRepository models,
    TrainingProviderRepository providers,
    ILogger<TrainingModelsController>? logger = null) : ControllerBase
{
    /// <summary>模型 ID 最大长度（trim 后，与契约 <c>TrainingModel.model</c> 一致）。</summary>
    internal const int MaxModelLength = 128;

    private readonly ILogger<TrainingModelsController> _logger =
        logger ?? NullLogger<TrainingModelsController>.Instance;

    /// <summary>模型列表：<c>enabled?</c> 过滤、CreatedAt 升序；<c>slug = {providerName}/{model}</c>（Provider 缺失则空名）。</summary>
    [HttpGet]
    public async Task<ActionResult<TrainingModelListDto>> List(
        [FromQuery] bool? enabled, CancellationToken ct = default)
    {
        var list = await models.ListAsync(enabled, ct);
        var providerNames = (await providers.ListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);

        var items = list
            .Select(m => TrainingMapping.ToModelDto(
                m, providerNames.GetValueOrDefault(m.ProviderId, string.Empty)))
            .ToList();
        return Ok(new TrainingModelListDto(items));
    }

    /// <summary>手动添加模型：Provider 不存在 404、model 空/超长 400、重复 409。</summary>
    [HttpPost]
    public async Task<ActionResult<TrainingModelDto>> Create(
        [FromBody] TrainingModelCreateRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(request?.ProviderId, out var providerId)
            || await providers.GetByIdAsync(providerId, ct) is not { } provider)
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        if (ValidateModel(request?.Model, out var model) is { } modelError)
        {
            return BadRequest(modelError);
        }

        var now = DateTime.UtcNow;
        var record = new TrainingModelRecord
        {
            ProviderId = provider.Id,
            Model = model,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await models.InsertAsync(record, ct);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("model exists"));
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("model exists"));
        }

        _logger.LogInformation(
            "training model created: modelId={ModelId} providerId={ProviderId} model={Model}",
            record.Id, provider.Id, record.Model);
        return StatusCode(
            StatusCodes.Status201Created, TrainingMapping.ToModelDto(record, provider.Name));
    }

    /// <summary>更新模型：<c>model</c> 缺失不变、<c>enabled</c> 缺失不变；改名撞唯一索引 409；不存在 404。</summary>
    [HttpPut("{modelId}")]
    public async Task<ActionResult<TrainingModelDto>> Update(
        string modelId, [FromBody] TrainingModelUpdateRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(modelId, out var id)
            || await models.GetByIdAsync(id, ct) is not { } current)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        var update = Builders<TrainingModelRecord>.Update.Set(m => m.UpdatedAt, DateTime.UtcNow);

        if (request?.Model is not null)
        {
            if (ValidateModel(request.Model, out var model) is { } modelError)
            {
                return BadRequest(modelError);
            }

            update = update.Set(m => m.Model, model);
        }

        if (request?.Enabled is { } enabled)
        {
            update = update.Set(m => m.Enabled, enabled);
        }

        TrainingModelRecord? updated;
        try
        {
            updated = await models.UpdateAsync(id, update, ct);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("model exists"));
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("model exists"));
        }

        if (updated is null)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        var providerName = (await providers.GetByIdAsync(updated.ProviderId, ct))?.Name ?? string.Empty;
        _logger.LogInformation(
            "training model updated: modelId={ModelId} model={Model} enabled={Enabled}",
            id, updated.Model, updated.Enabled);
        return Ok(TrainingMapping.ToModelDto(updated, providerName));
    }

    /// <summary>删除模型：204 / 404。</summary>
    [HttpDelete("{modelId}")]
    public async Task<ActionResult> Delete(string modelId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(modelId, out var id) || !await models.DeleteAsync(id, ct))
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        _logger.LogInformation("training model deleted: modelId={ModelId}", id);
        return NoContent();
    }

    /// <summary>模型名校验：trim 后为空 → <c>model is required</c>，长度 &gt; 128 → <c>model too long</c>。</summary>
    private static ErrorResponse? ValidateModel(string? model, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ErrorResponse("model is required");
        }

        var trimmed = model.Trim();
        if (trimmed.Length > MaxModelLength)
        {
            return new ErrorResponse("model too long");
        }

        normalized = trimmed;
        return null;
    }

    private static bool IsDuplicateKey(MongoWriteException ex) =>
        ex.WriteError?.Category == ServerErrorCategory.DuplicateKey;

    private static bool IsDuplicateKey(MongoBulkWriteException ex) =>
        ex.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey);
}
