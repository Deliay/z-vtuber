using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 模型 Provider API（契约 <c>contracts/training.openapi.yaml</c>，接口 1-6）：
/// 列表 / 新建 / 更新 / 删除（级联删其下 Models）/ 从 Endpoint 同步模型 / 批量导入模型。
/// 错误体统一 <c>{"error": "..."}</c>；<c>apiKey</c> 只进不出（响应仅 <c>apiKeySet</c>），绝不入日志。
/// </summary>
[ApiController]
[Route("api/training/providers")]
public sealed class TrainingProvidersController(
    TrainingProviderRepository providers,
    TrainingModelRepository models,
    IModelDiscoveryClient discovery,
    ILogger<TrainingProvidersController>? logger = null) : ControllerBase
{
    /// <summary>Provider 名称最大长度（trim 后，与契约 <c>TrainingProvider.name</c> 一致）。</summary>
    internal const int MaxNameLength = 64;

    /// <summary>Endpoint 最大长度（trim 后）。</summary>
    internal const int MaxEndpointLength = 512;

    private readonly ILogger<TrainingProvidersController> _logger =
        logger ?? NullLogger<TrainingProvidersController>.Instance;

    /// <summary>Provider 列表：CreatedAt 升序；永不回传 <c>apiKey</c>。</summary>
    [HttpGet]
    public async Task<ActionResult<TrainingProviderListDto>> List(CancellationToken ct = default)
    {
        var list = await providers.ListAsync(ct);
        return Ok(new TrainingProviderListDto(list.Select(TrainingMapping.ToProviderDto).ToList()));
    }

    /// <summary>新建 Provider：name 唯一、type 白名单、endpoint http(s) 去尾 <c>/</c>、apiKey 必填；重名 409。</summary>
    [HttpPost]
    public async Task<ActionResult<TrainingProviderDto>> Create(
        [FromBody] TrainingProviderCreateRequest? request, CancellationToken ct = default)
    {
        if (ValidateName(request?.Name, out var name) is { } nameError)
        {
            return BadRequest(nameError);
        }

        if (ValidateType(request?.Type, out var type) is { } typeError)
        {
            return BadRequest(typeError);
        }

        if (ValidateEndpoint(request?.Endpoint, out var endpoint) is { } endpointError)
        {
            return BadRequest(endpointError);
        }

        if (string.IsNullOrWhiteSpace(request?.ApiKey))
        {
            return BadRequest(new ErrorResponse("api key is required"));
        }

        var now = DateTime.UtcNow;
        var record = new TrainingProviderRecord
        {
            Name = name,
            Type = type,
            Endpoint = endpoint,
            ApiKey = request!.ApiKey!,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await providers.InsertAsync(record, ct);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("provider name exists"));
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("provider name exists"));
        }

        _logger.LogInformation(
            "training provider created: providerId={ProviderId} name={Name} endpoint={Endpoint}",
            record.Id, record.Name, record.Endpoint);
        return StatusCode(StatusCodes.Status201Created, TrainingMapping.ToProviderDto(record));
    }

    /// <summary>更新 Provider：字段缺失不变、<c>apiKey</c> 空串不改；重名 409；不存在 404。</summary>
    [HttpPut("{providerId}")]
    public async Task<ActionResult<TrainingProviderDto>> Update(
        string providerId, [FromBody] TrainingProviderUpdateRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(providerId, out var id) || await providers.GetByIdAsync(id, ct) is null)
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        var update = Builders<TrainingProviderRecord>.Update.Set(p => p.UpdatedAt, DateTime.UtcNow);

        if (request?.Name is not null)
        {
            if (ValidateName(request.Name, out var name) is { } nameError)
            {
                return BadRequest(nameError);
            }

            update = update.Set(p => p.Name, name);
        }

        if (request?.Type is not null)
        {
            if (ValidateType(request.Type, out var type) is { } typeError)
            {
                return BadRequest(typeError);
            }

            update = update.Set(p => p.Type, type);
        }

        if (request?.Endpoint is not null)
        {
            if (ValidateEndpoint(request.Endpoint, out var endpoint) is { } endpointError)
            {
                return BadRequest(endpointError);
            }

            update = update.Set(p => p.Endpoint, endpoint);
        }

        // apiKey 空串（含纯空白）视为不改：避免前端「不回显」导致误清空
        if (!string.IsNullOrWhiteSpace(request?.ApiKey))
        {
            update = update.Set(p => p.ApiKey, request.ApiKey);
        }

        TrainingProviderRecord? updated;
        try
        {
            updated = await providers.UpdateAsync(id, update, ct);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("provider name exists"));
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            return Conflict(new ErrorResponse("provider name exists"));
        }

        if (updated is null)
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        _logger.LogInformation(
            "training provider updated: providerId={ProviderId} name={Name}", id, updated.Name);
        return Ok(TrainingMapping.ToProviderDto(updated));
    }

    /// <summary>删除 Provider：先级联删其下 Models 再删 Provider；204 / 404（已入队任务保留 slug 快照）。</summary>
    [HttpDelete("{providerId}")]
    public async Task<ActionResult> Delete(string providerId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(providerId, out var id) || await providers.GetByIdAsync(id, ct) is null)
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        var cascaded = await models.DeleteByProviderAsync(id, ct);
        if (!await providers.DeleteAsync(id, ct))
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        _logger.LogInformation(
            "training provider deleted: providerId={ProviderId} cascadedModels={Cascaded}", id, cascaded);
        return NoContent();
    }

    /// <summary>从 Endpoint 同步模型列表；不可达/超时 502 <c>provider service unavailable</c>，响应非法 502 <c>invalid provider response</c>。</summary>
    [HttpGet("{providerId}/models/discover")]
    public async Task<ActionResult<TrainingModelDiscoverResultDto>> Discover(
        string providerId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(providerId, out var id)
            || await providers.GetByIdAsync(id, ct) is not { } provider)
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        IReadOnlyList<string> discovered;
        try
        {
            discovered = await discovery.DiscoverAsync(provider, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "training provider discover failed: provider service unreachable");
            return ProviderServiceUnavailable();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderServiceUnavailable();
        }
        catch (InvalidProviderResponseException ex)
        {
            _logger.LogWarning(ex, "training provider discover failed: invalid provider response");
            return InvalidProviderResponse();
        }
        catch (ProviderTypeException ex)
        {
            _logger.LogWarning(ex, "training provider discover failed: unsupported provider type");
            return InvalidProviderResponse();
        }

        var result = new TrainingModelDiscoverResultDto(
            discovered.Select(m => new TrainingDiscoveredModelDto(m)).ToList());
        _logger.LogInformation(
            "training provider models discovered: providerId={ProviderId} endpoint={Endpoint} count={Count}",
            id, provider.Endpoint, discovered.Count);
        return Ok(result);
    }

    /// <summary>批量导入模型：空白项与唯一键冲突计 skipped，返回 <c>{imported, skipped}</c>。</summary>
    [HttpPost("{providerId}/models/import")]
    public async Task<ActionResult<TrainingModelImportResultDto>> Import(
        string providerId, [FromBody] TrainingModelImportRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(providerId, out var id)
            || await providers.GetByIdAsync(id, ct) is not { } provider)
        {
            return NotFound(new ErrorResponse("provider not found"));
        }

        if (request?.Models is null || request.Models.Count == 0)
        {
            return BadRequest(new ErrorResponse("models is required"));
        }

        var now = DateTime.UtcNow;
        var records = new List<TrainingModelRecord>();
        var invalid = 0L;
        foreach (var raw in request.Models)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                invalid++;
                continue;
            }

            // 与手动添加模型同口径：trim 后长度 > 128 → 400 model too long（不得绕过校验）
            var model = raw.Trim();
            if (model.Length > TrainingModelsController.MaxModelLength)
            {
                return BadRequest(new ErrorResponse("model too long"));
            }

            records.Add(new TrainingModelRecord
            {
                ProviderId = provider.Id,
                Model = model,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var (imported, skipped) = await models.InsertManyIgnoreDuplicatesAsync(records, ct);
        _logger.LogInformation(
            "training provider models imported: providerId={ProviderId} endpoint={Endpoint} imported={Imported} skipped={Skipped}",
            provider.Id, provider.Endpoint, imported, skipped + invalid);
        return Ok(new TrainingModelImportResultDto(imported, skipped + invalid));
    }

    /// <summary>名字校验：trim 后为空 → <c>name is required</c>，长度 &gt; 64 → <c>name too long</c>。</summary>
    private static ErrorResponse? ValidateName(string? name, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ErrorResponse("name is required");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            return new ErrorResponse("name too long");
        }

        normalized = trimmed;
        return null;
    }

    /// <summary>类型白名单 <c>openai|claude</c>（大小写不敏感，归一化小写）。</summary>
    private static ErrorResponse? ValidateType(string? type, out string normalized)
    {
        normalized = string.Empty;
        var candidate = type?.Trim().ToLowerInvariant();
        if (candidate is not (TrainingProviderTypes.OpenAi or TrainingProviderTypes.Claude))
        {
            return new ErrorResponse("unsupported provider type");
        }

        normalized = candidate;
        return null;
    }

    /// <summary>Endpoint 校验：trim 后非空 ≤512，仅 http/https，归一化去尾 <c>/</c>。</summary>
    private static ErrorResponse? ValidateEndpoint(string? endpoint, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ErrorResponse("endpoint is required");
        }

        var trimmed = endpoint.Trim();
        if (trimmed.Length > MaxEndpointLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ErrorResponse("invalid endpoint");
        }

        normalized = trimmed.TrimEnd('/');
        return null;
    }

    private ObjectResult ProviderServiceUnavailable() =>
        StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("provider service unavailable"));

    private ObjectResult InvalidProviderResponse() =>
        StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("invalid provider response"));

    private static bool IsDuplicateKey(MongoWriteException ex) =>
        ex.WriteError?.Category == ServerErrorCategory.DuplicateKey;

    private static bool IsDuplicateKey(MongoBulkWriteException ex) =>
        ex.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey);
}
