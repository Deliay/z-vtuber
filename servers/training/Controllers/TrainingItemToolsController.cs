using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Ai.Prompts;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 数据集条目工具 API（契约 <c>contracts/training.openapi.yaml</c>，接口 11-12）：
/// 单条「重新识别」（取音频转 base64 调模型要求 JSON 输出，仅返回文本不写库）、
/// 批量提交「情绪识别」任务（逐条去重/空文本跳过，建 PENDING 任务）。错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/datasets/{datasetId}/items")]
public sealed class TrainingItemToolsController(
    TrainingDatasetRepository datasets,
    TrainingDatasetItemRepository items,
    TrainingModelRepository models,
    TrainingProviderRepository providers,
    IRecordingAudioClient audio,
    IChatModelClientFactory aiFactory,
    TrainingEmotionTaskRepository tasks,
    ILogger<TrainingItemToolsController>? logger = null) : ControllerBase
{
    private readonly ILogger<TrainingItemToolsController> _logger =
        logger ?? NullLogger<TrainingItemToolsController>.Instance;

    /// <summary>
    /// 单条重新识别：dataset → item → model（含 enabled）校验 → 取音频转 base64 → 识别 prompt 调模型 →
    /// 解析 JSON → 200 <c>{text}</c>（**不写库**，保存由前端走更新条目端点）。
    /// </summary>
    [HttpPost("{itemId}/recognize")]
    public async Task<ActionResult<TrainingRecognizeResultDto>> Recognize(
        string datasetId, string itemId, [FromBody] TrainingRecognizeRequest? request,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var datasetObjectId)
            || await datasets.GetByIdAsync(datasetObjectId, ct) is null)
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        if (!ObjectId.TryParse(itemId, out var itemObjectId)
            || await items.GetByIdAsync(datasetObjectId, itemObjectId, ct) is not { } item)
        {
            return NotFound(new ErrorResponse("item not found"));
        }

        if (!ObjectId.TryParse(request?.ModelId, out var modelId)
            || await models.GetByIdAsync(modelId, ct) is not { } model)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        if (!model.Enabled)
        {
            return BadRequest(new ErrorResponse("model disabled"));
        }

        if (await providers.GetByIdAsync(model.ProviderId, ct) is not { } provider)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        AudioClip clip;
        try
        {
            clip = await audio.GetClipAsync(item.AudioUri, item.StartMs, item.EndMs, ct);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ErrorResponse("audio uri is empty"));
        }
        catch (AudioUnavailableException ex)
        {
            _logger.LogWarning(ex, "training item recognize failed: audio unavailable");
            return AudioUnavailable();
        }
        catch (AudioTooLargeException ex)
        {
            _logger.LogWarning(ex, "training item recognize failed: audio too large");
            return BadRequest(new ErrorResponse("audio too large"));
        }

        IChatModelClient client;
        try
        {
            client = aiFactory.Create(provider.Type);
        }
        catch (ProviderTypeException ex)
        {
            _logger.LogWarning(ex, "training item recognize failed: unsupported provider type");
            return BadRequest(new ErrorResponse("unsupported provider type"));
        }

        var prompt = RecognizePromptBuilder.Build(
            item.Text, new ChatAudio(Convert.ToBase64String(clip.Bytes), clip.ContentType));

        var stopwatch = Stopwatch.StartNew();
        string raw;
        try
        {
            raw = await client.CompleteJsonAsync(provider, model, prompt, ct);
        }
        catch (ModelAudioNotSupportedException)
        {
            return BadRequest(new ErrorResponse("model does not support audio input"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "training item recognize failed: model service unavailable");
            return ModelServiceUnavailable();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelServiceUnavailable();
        }
        catch (ModelOutputException ex)
        {
            _logger.LogWarning(ex, "training item recognize failed: invalid model output");
            return InvalidModelOutput();
        }

        string text;
        try
        {
            text = ModelJsonParser.ParseRecognizeText(raw);
        }
        catch (ModelOutputException ex)
        {
            _logger.LogWarning(ex, "training item recognize failed: invalid model output");
            return InvalidModelOutput();
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "training item recognize: datasetId={DatasetId} itemId={ItemId} modelId={ModelId} bytes={Bytes} elapsedMs={ElapsedMs}",
            datasetId, itemId, modelId, clip.Bytes.Length, stopwatch.ElapsedMilliseconds);
        return Ok(new TrainingRecognizeResultDto(text));
    }

    /// <summary>
    /// 提交情绪识别任务：dataset / model 校验 → 逐条（非法 id/条目不存在/空文本/已有活跃任务）跳过 →
    /// 建 PENDING 任务（含 <c>modelSlug</c>/<c>audioKey</c> 快照）→ 200 <c>{created, skipped}</c>。
    /// </summary>
    [HttpPost("emotion-tasks")]
    public async Task<ActionResult<TrainingEmotionTaskCreateResultDto>> CreateEmotionTasks(
        string datasetId, [FromBody] TrainingEmotionTaskCreateRequest? request,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var datasetObjectId)
            || await datasets.GetByIdAsync(datasetObjectId, ct) is null)
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        if (!ObjectId.TryParse(request?.ModelId, out var modelId)
            || await models.GetByIdAsync(modelId, ct) is not { } model)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        if (await providers.GetByIdAsync(model.ProviderId, ct) is not { } provider)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        if (request?.ItemIds is null || request.ItemIds.Count == 0)
        {
            return BadRequest(new ErrorResponse("itemIds is required"));
        }

        var modelSlug = TrainingMapping.BuildModelSlug(provider.Name, model.Model);
        var created = 0L;
        var skipped = 0L;

        foreach (var rawItemId in request.ItemIds)
        {
            if (!ObjectId.TryParse(rawItemId, out var itemObjectId))
            {
                skipped++;
                continue;
            }

            var item = await items.GetByIdAsync(datasetObjectId, itemObjectId, ct);
            if (item is null)
            {
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                _logger.LogInformation(
                    "training item emotion task skipped: itemId={ItemId} reason=empty text", itemObjectId);
                skipped++;
                continue;
            }

            if (await tasks.HasActiveAsync(itemObjectId, ct))
            {
                skipped++;
                continue;
            }

            await tasks.InsertAsync(new TrainingEmotionTaskRecord
            {
                DatasetId = datasetObjectId,
                ItemId = itemObjectId,
                AudioKey = item.AudioKey,
                ModelId = modelId,
                ModelSlug = modelSlug,
                Status = TrainingEmotionTaskStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            }, ct);
            created++;
        }

        _logger.LogInformation(
            "training item emotion tasks submitted: datasetId={DatasetId} modelId={ModelId} created={Created} skipped={Skipped}",
            datasetId, modelId, created, skipped);
        return Ok(new TrainingEmotionTaskCreateResultDto(created, skipped));
    }

    private ObjectResult AudioUnavailable() =>
        StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("audio unavailable"));

    private ObjectResult ModelServiceUnavailable() =>
        StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("model service unavailable"));

    private ObjectResult InvalidModelOutput() =>
        StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("invalid model output"));
}
