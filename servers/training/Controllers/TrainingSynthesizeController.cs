using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.FeaturePoints;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 数据集音频生成 API（契约 <c>contracts/training.openapi.yaml</c>）：
/// <c>POST synthesize</c>（文本 + 数据集参考池 + 语音大模型 → <c>audio/wav</c>）。错误体统一 <c>{"error": "..."}</c>。
/// </summary>
[ApiController]
[Route("api/training/datasets/{datasetId}")]
public sealed class TrainingSynthesizeController(
    TrainingDatasetRepository datasets,
    TrainingModelRepository models,
    TrainingProviderRepository providers,
    SynthesizeService synthesize,
    SpeechOptions speechOptions,
    ISpeechModelClientFactory speechFactory,
    ITrainingModelResolver modelResolver,
    ILogger<TrainingSynthesizeController>? logger = null) : ControllerBase
{
    /// <summary>语音合成支持的语种白名单（大小写不敏感）。</summary>
    internal static readonly string[] SupportedLangs = SpeechOptions.SupportedLangs;

    private readonly ILogger<TrainingSynthesizeController> _logger =
        logger ?? NullLogger<TrainingSynthesizeController>.Instance;

    /// <summary>
    /// 音频生成：dataset（404）→ text（400）→ lang（400）→ emotionMode（400）→（llm）情绪 model（404/400）→
    /// 语音模型解析（<c>speech-synthesize</c> 功能点，无可用 409，provider 无音频输出能力 400）→
    /// <see cref="SynthesizeService"/>（拼音断句多命中拼接/无候选回退字符相似/参考取数/语音大模型）→
    /// 200 <c>audio/wav</c> + <c>X-Speaker-Audio-Key</c>/<c>X-Emo-Audio-Key</c> 命中头。
    /// query <c>emotionMode</c> 缺省 <c>none</c>（不传情绪参考）；<c>llm</c> 用大模型生成情绪参考；
    /// <c>dataset</c> 与 <c>none</c> 均跳过情绪模型解析（<c>dataset</c> 用音色命中条目自身情绪）。
    /// </summary>
    [HttpPost("synthesize")]
    public async Task<IActionResult> Synthesize(
        string datasetId, [FromBody] TrainingSynthesizeRequest? request,
        [FromQuery] string? emotionMode = null, CancellationToken ct = default)
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

        // 缺省语种取 Speech:DefaultLang，与显式 lang 同样过白名单（脏配置不得直传上游）。
        var lang = (string.IsNullOrWhiteSpace(request?.Lang) ? speechOptions.DefaultLang : request.Lang)
            .Trim().ToUpperInvariant();
        if (!SupportedLangs.Contains(lang, StringComparer.Ordinal))
        {
            return BadRequest(new ErrorResponse("invalid lang"));
        }

        // 情绪来源：缺省 none（不传情绪参考）；llm 用大模型；dataset 直接用命中条目情绪。
        if (!TryParseEmotionMode(emotionMode, out var mode))
        {
            return BadRequest(new ErrorResponse("invalid emotion mode"));
        }

        TrainingModelRecord? emotionModel = null;
        if (mode == EmotionMode.Llm)
        {
            var resolved = await ResolveEmotionModelAsync(request?.ModelId, ct);
            if (resolved.Error is not null)
            {
                return resolved.Error;
            }

            emotionModel = resolved.Model;
        }

        // 语音合成模型解析（先于音色参考挑选：无语音模型优先于无参考语音返回）。
        var speech = await ResolveSpeechModelAsync(ct);
        if (speech.Error is not null)
        {
            return speech.Error;
        }

        try
        {
            var result = await synthesize.SynthesizeAsync(
                datasetObjectId, text, lang, emotionModel, speech.Speech!, mode, ct);
            if (result.SpeakerAudioKeys.Count > 0)
            {
                Response.Headers["X-Speaker-Audio-Key"] = string.Join(',', result.SpeakerAudioKeys);
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
        catch (AudioUnavailableException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: audio unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("audio unavailable"));
        }
        catch (SpeechModelUnavailableException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: model service unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("model service unavailable"));
        }
        catch (ModelOutputException ex)
        {
            _logger.LogWarning(ex, "training synthesize failed: invalid model output");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("invalid model output"));
        }
    }

    /// <summary>
    /// 解析语音合成模型：功能点 <c>speech-synthesize</c>（输入 <c>audio,text</c> / 输出 <c>audio</c>）指定优先 → 默认；
    /// 无可用 → 409 <c>no speech model available</c>；工厂派发无音频输出能力（Claude-like 等）→ 400
    /// <c>model does not support audio output</c>。
    /// </summary>
    private async Task<(SpeechSynthesisModel? Speech, ObjectResult? Error)> ResolveSpeechModelAsync(
        CancellationToken ct)
    {
        var definition = FeaturePointIds.SpeechSynthesizeDefinition;
        var resolution = await modelResolver.ResolveAsync(
            definition.Slug, definition.Name,
            definition.InputCapabilities, definition.OutputCapabilities, ct);

        if (resolution.ModelId is not { } resolvedId
            || await models.GetByIdAsync(resolvedId, ct) is not { } model
            || await providers.GetByIdAsync(model.ProviderId, ct) is not { } provider)
        {
            return (null, StatusCode(StatusCodes.Status409Conflict, new ErrorResponse("no speech model available")));
        }

        try
        {
            var client = speechFactory.Create(provider.Type);
            return (new SpeechSynthesisModel(client, provider, model), null);
        }
        catch (ModelAudioOutputNotSupportedException)
        {
            return (null, BadRequest(new ErrorResponse("model does not support audio output")));
        }
        catch (ProviderTypeException)
        {
            return (null, BadRequest(new ErrorResponse("model does not support audio output")));
        }
    }

    /// <summary>
    /// 解析情感向量生成所用模型：显式 <paramref name="rawModelId"/>（非法/不存在 404、停用 400）；
    /// 否则按功能点 <c>emotion-from-text</c> 解析（指定优先 → 默认选择 → 无）；无可用模型 → null
    /// （情绪降级中性）。
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

        var definition = FeaturePointIds.EmotionFromTextDefinition;
        var resolution = await modelResolver.ResolveAsync(
            definition.Slug, definition.Name,
            definition.InputCapabilities, definition.OutputCapabilities, ct);
        if (resolution.ModelId is not { } resolvedId)
        {
            return (null, null);
        }

        return (await models.GetByIdAsync(resolvedId, ct), null);
    }

    /// <summary>
    /// 解析 query <c>emotionMode</c>：缺省/空 → <see cref="EmotionMode.None"/>（不传情绪参考）；
    /// 大小写不敏感匹配 <c>llm</c>/<c>dataset</c>/<c>none</c>；其他 → false（控制器映射 400 <c>invalid emotion mode</c>）。
    /// </summary>
    internal static bool TryParseEmotionMode(string? raw, out EmotionMode mode)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "none":
                mode = EmotionMode.None;
                return true;
            case "llm":
                mode = EmotionMode.Llm;
                return true;
            case "dataset":
                mode = EmotionMode.Dataset;
                return true;
            default:
                mode = EmotionMode.None;
                return false;
        }
    }
}
