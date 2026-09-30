using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 数据集音频生成 API（契约 <c>contracts/training.openapi.yaml</c>）：
/// <c>POST synthesize</c>（文本 + 数据集参考池 → <c>audio/wav</c>）、
/// <c>POST vectors/rebuild</c>（MongoDB 条目 → Milvus 向量索引重建）。错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/datasets/{datasetId}")]
public sealed class TrainingSynthesizeController(
    TrainingDatasetRepository datasets,
    TrainingModelRepository models,
    SynthesizeService synthesize,
    VectorSyncService vectors,
    TtsOptions ttsOptions,
    SynthesizeOptions synthesizeOptions,
    ILogger<TrainingSynthesizeController>? logger = null) : ControllerBase
{
    /// <summary>上游 index-tts-api 支持的语种白名单（大小写不敏感）。</summary>
    internal static readonly string[] SupportedLangs = TtsOptions.SupportedLangs;

    private readonly ILogger<TrainingSynthesizeController> _logger =
        logger ?? NullLogger<TrainingSynthesizeController>.Instance;

    /// <summary>
    /// 音频生成：dataset（404）→ text（400）→ lang（400）→ model（404/400）校验 →
    /// <see cref="SynthesizeService"/>（embedding/向量检索/参考取数/TTS）→ 200 <c>audio/wav</c> +
    /// <c>X-Speaker-Audio-Key</c>/<c>X-Emo-Audio-Key</c> 命中头。
    /// </summary>
    [HttpPost("synthesize")]
    public async Task<IActionResult> Synthesize(
        string datasetId, [FromBody] TrainingSynthesizeRequest? request, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var datasetObjectId)
            || await datasets.GetByIdAsync(datasetObjectId, ct) is null)
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        var text = request?.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return BadRequest(new ErrorResponse("text is required"));
        }

        // 缺省语种取 Tts:DefaultLang，与显式 lang 同样过白名单（脏配置不得直传上游）。
        var lang = (string.IsNullOrWhiteSpace(request?.Lang) ? ttsOptions.DefaultLang : request.Lang)
            .Trim().ToUpperInvariant();
        if (!SupportedLangs.Contains(lang, StringComparer.Ordinal))
        {
            return BadRequest(new ErrorResponse("invalid lang"));
        }

        var model = await ResolveEmotionModelAsync(request?.ModelId, ct);
        if (model.Error is not null)
        {
            return model.Error;
        }

        try
        {
            var result = await synthesize.SynthesizeAsync(datasetObjectId, text, lang, model.Model, ct);
            if (!string.IsNullOrEmpty(result.SpeakerAudioKey))
            {
                Response.Headers["X-Speaker-Audio-Key"] = result.SpeakerAudioKey;
            }

            if (!string.IsNullOrEmpty(result.EmoAudioKey))
            {
                Response.Headers["X-Emo-Audio-Key"] = result.EmoAudioKey;
            }

            return File(result.Audio, "audio/wav");
        }
        catch (NoSpeakerReferenceException)
        {
            return StatusCode(StatusCodes.Status409Conflict, new ErrorResponse("no speaker reference"));
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: embedding service unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("embedding service unavailable"));
        }
        catch (VectorStoreUnavailableException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: vector store unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("vector store unavailable"));
        }
        catch (AudioUnavailableException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: audio unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("audio unavailable"));
        }
        catch (TtsUnavailableException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: tts service unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("tts service unavailable"));
        }
    }

    /// <summary>
    /// 向量索引重建：dataset（404）校验 → <see cref="VectorSyncService.RebuildAsync"/> →
    /// 200 <c>{"updated": n}</c>；embedding/向量库不可用 → 502。
    /// </summary>
    [HttpPost("vectors/rebuild")]
    public async Task<ActionResult<TrainingVectorsRebuildResultDto>> RebuildVectors(
        string datasetId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(datasetId, out var datasetObjectId)
            || await datasets.GetByIdAsync(datasetObjectId, ct) is null)
        {
            return NotFound(new ErrorResponse("dataset not found"));
        }

        try
        {
            var updated = await vectors.RebuildAsync(datasetObjectId, ct);
            return Ok(new TrainingVectorsRebuildResultDto(updated));
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogWarning(ex, "training vectors rebuild failed: embedding service unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("embedding service unavailable"));
        }
        catch (VectorStoreUnavailableException ex)
        {
            _logger.LogWarning(ex, "training vectors rebuild failed: vector store unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("vector store unavailable"));
        }
    }

    /// <summary>
    /// 解析情感向量生成所用模型：显式 <paramref name="rawModelId"/>（非法/不存在 404、停用 400）；
    /// 否则 <c>Synthesize:DefaultEmotionModelId</c>，再否则首个启用模型；均无 → null（情绪降级中性）。
    /// </summary>
    private async Task<(TrainingModelRecord? Model, ObjectResult? Error)> ResolveEmotionModelAsync(
        string? rawModelId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(rawModelId))
        {
            if (!ObjectId.TryParse(rawModelId, out var modelId)
                || await models.GetByIdAsync(modelId, ct) is not { } model)
            {
                return (null, NotFound(new ErrorResponse("model not found")));
            }

            return model.Enabled
                ? (model, null)
                : (null, BadRequest(new ErrorResponse("model disabled")));
        }

        if (!string.IsNullOrWhiteSpace(synthesizeOptions.DefaultEmotionModelId)
            && ObjectId.TryParse(synthesizeOptions.DefaultEmotionModelId, out var defaultId)
            && await models.GetByIdAsync(defaultId, ct) is { Enabled: true } configured)
        {
            return (configured, null);
        }

        var fallback = (await models.ListAsync(enabled: true, ct)).FirstOrDefault();
        return (fallback, null);
    }
}
