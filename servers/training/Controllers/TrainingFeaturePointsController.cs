using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Contracts;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.FeaturePoints;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 功能点模型设置 API（契约 <c>contracts/training.openapi.yaml</c>，接口 4-7）：
/// 列表（实时计算指定可用性/默认选择）、请求模型（slug 自动登记 + 指定/默认解析）、
/// 指定模型（仅可选可用模型）、取消指定（恢复未设置）。错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/feature-points")]
public sealed class TrainingFeaturePointsController(
    TrainingFeaturePointRepository featurePoints,
    TrainingModelRepository models,
    TrainingProviderRepository providers,
    ITrainingModelResolver resolver,
    ILogger<TrainingFeaturePointsController>? logger = null) : ControllerBase
{
    /// <summary>功能点 slug 最大长度（trim 后，与模型名同口径）。</summary>
    internal const int MaxSlugLength = 128;

    private readonly ILogger<TrainingFeaturePointsController> _logger =
        logger ?? NullLogger<TrainingFeaturePointsController>.Instance;

    /// <summary>功能点列表（Slug 升序）：实时计算 <c>specifiedModelAvailable</c> 与默认选择。</summary>
    [HttpGet]
    public async Task<ActionResult<TrainingFeaturePointListDto>> List(CancellationToken ct = default)
    {
        var records = await featurePoints.ListAsync(ct);
        var providerNames = (await providers.ListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
        var allModels = await models.ListAsync(ct: ct);

        var items = records
            .Select(r => BuildDto(r, providerNames, allModels))
            .ToList();
        return Ok(new TrainingFeaturePointListDto(items));
    }

    /// <summary>功能点请求模型：自动登记（upsert）后按指定优先 → 默认 → 无解析。</summary>
    [HttpPost("{slug}/model/resolve")]
    public async Task<ActionResult<TrainingFeaturePointModelDto>> Resolve(
        string slug, [FromBody] TrainingFeaturePointResolveRequest? request, CancellationToken ct = default)
    {
        if (ValidateSlug(slug, out var normalizedSlug) is { } slugError)
        {
            return BadRequest(slugError);
        }

        IReadOnlyList<string> input;
        IReadOnlyList<string> output;
        try
        {
            input = TrainingModelCapabilities.Normalize(request?.InputCapabilities);
            output = TrainingModelCapabilities.Normalize(request?.OutputCapabilities);
        }
        catch (CapabilityException)
        {
            return BadRequest(new ErrorResponse("unsupported capability"));
        }

        // 契约 TrainingFeaturePointResolveRequest 声明能力数组必填：缺失/空（含全空白）一律 400，
        // 杜绝登记出「无能力要求」功能点（空要求会使空能力模型误入候选，违反 PRD 功能点 1 验收）。
        if (request?.InputCapabilities is null || input.Count == 0)
        {
            return BadRequest(new ErrorResponse("inputCapabilities is required"));
        }

        if (request.OutputCapabilities is null || output.Count == 0)
        {
            return BadRequest(new ErrorResponse("outputCapabilities is required"));
        }

        var resolution = await resolver.ResolveAsync(normalizedSlug, request?.Name, input, output, ct);
        _logger.LogInformation(
            "training feature point resolved: slug={Slug} source={Source} modelId={ModelId}",
            resolution.Slug, resolution.Source, resolution.ModelId);
        return Ok(new TrainingFeaturePointModelDto(
            resolution.Slug,
            resolution.ModelId?.ToString(),
            resolution.ModelSlug,
            resolution.Source));
    }

    /// <summary>指定模型：功能点未登记 404、模型不存在 404、非可用模型 400 <c>model not qualified</c>。</summary>
    [HttpPut("{slug}/model")]
    public async Task<ActionResult<TrainingFeaturePointDto>> SetModel(
        string slug, [FromBody] TrainingFeaturePointSetModelRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(request?.ModelId, out var modelId))
        {
            return BadRequest(new ErrorResponse("modelId is required"));
        }

        if (await featurePoints.GetBySlugAsync(slug, ct) is not { } record)
        {
            return NotFound(new ErrorResponse("feature point not found"));
        }

        if (await models.GetByIdAsync(modelId, ct) is not { } model)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        if (!model.Enabled
            || !TrainingModelCapabilities.Matches(model.InputCapabilities, record.InputCapabilities ?? [])
            || !TrainingModelCapabilities.Matches(model.OutputCapabilities, record.OutputCapabilities ?? []))
        {
            return BadRequest(new ErrorResponse("model not qualified"));
        }

        var updated = await featurePoints.SetModelAsync(slug, modelId, DateTime.UtcNow, ct)
            ?? record;
        _logger.LogInformation(
            "training feature point model set: slug={Slug} modelId={ModelId}", slug, modelId);
        return Ok(await BuildDtoAsync(updated, ct));
    }

    /// <summary>取消指定（置空，恢复未设置）；功能点未登记 404。</summary>
    [HttpDelete("{slug}/model")]
    public async Task<ActionResult<TrainingFeaturePointDto>> ClearModel(
        string slug, CancellationToken ct = default)
    {
        if (await featurePoints.GetBySlugAsync(slug, ct) is not { } record)
        {
            return NotFound(new ErrorResponse("feature point not found"));
        }

        var updated = await featurePoints.SetModelAsync(slug, null, DateTime.UtcNow, ct) ?? record;
        _logger.LogInformation("training feature point model cleared: slug={Slug}", slug);
        return Ok(await BuildDtoAsync(updated, ct));
    }

    /// <summary>slug 校验：trim 后为空 → <c>feature point slug is required</c>，长度 &gt; 128 → <c>feature point slug too long</c>。</summary>
    private static ErrorResponse? ValidateSlug(string? slug, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return new ErrorResponse("feature point slug is required");
        }

        var trimmed = slug.Trim();
        if (trimmed.Length > MaxSlugLength)
        {
            return new ErrorResponse("feature point slug too long");
        }

        normalized = trimmed;
        return null;
    }

    private async Task<TrainingFeaturePointDto> BuildDtoAsync(
        TrainingFeaturePointRecord record, CancellationToken ct)
    {
        var providerNames = (await providers.ListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
        var allModels = await models.ListAsync(ct: ct);
        return BuildDto(record, providerNames, allModels);
    }

    private static TrainingFeaturePointDto BuildDto(
        TrainingFeaturePointRecord record,
        IReadOnlyDictionary<ObjectId, string> providerNames,
        IReadOnlyList<TrainingModelRecord> allModels)
    {
        var requiredInput = record.InputCapabilities ?? [];
        var requiredOutput = record.OutputCapabilities ?? [];

        // allModels 已按 CreatedAt 升序（默认选择 = 首个可用模型）。
        var candidates = allModels
            .Where(m => m.Enabled
                && TrainingModelCapabilities.Matches(m.InputCapabilities, requiredInput)
                && TrainingModelCapabilities.Matches(m.OutputCapabilities, requiredOutput))
            .ToList();

        var specified = record.ModelId is { } specifiedId
            ? allModels.FirstOrDefault(m => m.Id == specifiedId)
            : null;
        var specifiedAvailable = specified is not null
            && specified.Enabled
            && TrainingModelCapabilities.Matches(specified.InputCapabilities, requiredInput)
            && TrainingModelCapabilities.Matches(specified.OutputCapabilities, requiredOutput);

        var defaultModel = candidates.Count > 0 ? candidates[0] : null;
        return TrainingMapping.ToFeaturePointDto(
            record, SlugOf(specified, providerNames), specifiedAvailable,
            defaultModel, SlugOf(defaultModel, providerNames));
    }

    private static string? SlugOf(
        TrainingModelRecord? model, IReadOnlyDictionary<ObjectId, string> providerNames) =>
        model is null
            ? null
            : TrainingMapping.BuildModelSlug(
                providerNames.GetValueOrDefault(model.ProviderId, string.Empty), model.Model);
}
