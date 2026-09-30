using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Ai.Prompts;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>合成结果：音频字节 + 命中的音色/情绪参考 audioKey（可观测）。</summary>
public sealed record SynthesizeResult(byte[] Audio, string? SpeakerAudioKey, string? EmoAudioKey);

/// <summary>数据集内无任何可用音色参考（无文本向量候选）→ 映射 409 <c>no speaker reference</c>。</summary>
public sealed class NoSpeakerReferenceException() : Exception("no speaker reference");

/// <summary>
/// 音频生成编排：文本向量检索 top-1 取 <c>speaker_prompt</c>（逐候选兜底、全失败 502）、
/// 情绪参考按 <see cref="EmotionMode"/> 取值——<see cref="EmotionMode.Llm"/> 用可用模型生成情感向量检索 top-1、
/// <see cref="EmotionMode.Dataset"/> 直接用命中条目自身的 <c>Extensions.emotion</c>（best-effort，失败降级中性）、
/// 调上游 TTS 返回 WAV。日志不打文本/音频内容。
/// </summary>
public sealed class SynthesizeService(
    TrainingDatasetItemRepository items,
    TrainingProviderRepository providers,
    IChatModelClientFactory aiFactory,
    IRecordingAudioClient audio,
    IVectorStore store,
    IEmbeddingClient embedding,
    ITtsClient tts,
    SynthesizeOptions synthesizeOptions,
    ILogger<SynthesizeService> logger)
{
    public async Task<SynthesizeResult> SynthesizeAsync(
        ObjectId datasetId, string text, string lang, TrainingModelRecord? emotionModel,
        EmotionMode emotionMode = EmotionMode.Llm, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1) 文本向量（本地 embedding）。
        var query = await embedding.GetEmbeddingAsync(text, ct);

        // 2) 音色参考检索（文本向量 COSINE top-k）。
        var speakerCandidates = await store.SearchTextAsync(
            datasetId, query, synthesizeOptions.ReferenceCandidateLimit, ct);
        if (speakerCandidates.Count == 0)
        {
            throw new NoSpeakerReferenceException();
        }

        // 3) 逐候选取数：首个可用音频为 speaker；dataset 模式同轮捕获首个带情绪标注的候选
        //    （llm 模式不需要，取到 speaker 即停）。
        var useDatasetEmotion = emotionMode == EmotionMode.Dataset;
        var (speaker, datasetEmotion) =
            await ScanCandidatesAsync(datasetId, speakerCandidates, useDatasetEmotion, ct);
        if (speaker is null)
        {
            throw new AudioUnavailableException("no speaker reference audio available");
        }

        // 4) 情绪参考：llm 用大模型生成查询向量后检索；dataset 直接用命中条目自身 Extensions.emotion。
        var emotion = useDatasetEmotion
            ? BuildDatasetEmotionReference(datasetEmotion)
            : await TryBuildEmotionReferenceAsync(datasetId, text, emotionModel, ct);

        // 5) 上游合成。
        var request = new TtsSynthesizeRequest(
            text,
            lang,
            new TtsSpeakerReference(speaker.Value.Clip.Bytes, speaker.Value.Clip.ContentType),
            emotion?.Reference);
        var bytes = await tts.SynthesizeAsync(request, ct);

        // 命中头回显条目记录（Mongo 事实源）的 audioKey，而非 Milvus 检索输出字段：
        // 后者若为畸形值会让响应头赋值抛异常，在 TTS 已成功后落成 500。
        var speakerKey = speaker.Value.Item.AudioKey;
        var emoKey = emotion?.AudioKey;
        stopwatch.Stop();
        logger.LogInformation(
            "training synthesize: datasetId={DatasetId} speakerKey={SpeakerKey} emoKey={EmoKey} lang={Lang} emotionMode={EmotionMode} elapsedMs={ElapsedMs}",
            datasetId, speakerKey, emoKey, lang, emotionMode, stopwatch.ElapsedMilliseconds);

        return new SynthesizeResult(bytes, speakerKey, emoKey);
    }

    /// <summary>
    /// dataset 模式：把命中候选（音频可取且带 <c>Extensions.emotion</c>）的标注组装为情绪参考；
    /// 无候选或标注缺失 → null（降级中性）。
    /// </summary>
    private static EmotionMatch? BuildDatasetEmotionReference(ReferenceMatch? source)
    {
        if (source is not { } match)
        {
            return null;
        }

        var emotion = ReadEmotion(match.Item);
        if (emotion is null)
        {
            return null;
        }

        return new EmotionMatch(
            new TtsEmotionReference(match.Clip.Bytes, match.Clip.ContentType, emotion),
            match.Item.AudioKey);
    }

    private async Task<EmotionMatch?> TryBuildEmotionReferenceAsync(
        ObjectId datasetId, string text, TrainingModelRecord? emotionModel, CancellationToken ct)
    {
        if (emotionModel is null)
        {
            return null;
        }

        try
        {
            var provider = await providers.GetByIdAsync(emotionModel.ProviderId, ct);
            if (provider is null)
            {
                logger.LogWarning(
                    "training synthesize: emotion provider not found providerId={ProviderId}",
                    emotionModel.ProviderId);
                return null;
            }

            var prompt = EmotionPromptBuilder.Build(text);
            var raw = await aiFactory.Create(provider.Type).CompleteJsonAsync(provider, emotionModel, prompt, ct);
            var generated = ModelJsonParser.ParseEmotion(raw, logger);
            var vector = EmotionVector.FromEmotion(generated);

            var candidates = await store.SearchEmotionAsync(
                datasetId, vector, synthesizeOptions.ReferenceCandidateLimit, ct);
            var (match, _) = await ScanCandidatesAsync(datasetId, candidates, captureDatasetEmotion: false, ct);
            if (match is null)
            {
                return null;
            }

            var matchedEmotion = ReadEmotion(match.Value.Item);
            if (matchedEmotion is null)
            {
                return null;
            }

            var reference = new TtsEmotionReference(
                match.Value.Clip.Bytes, match.Value.Clip.ContentType, matchedEmotion);
            return new EmotionMatch(reference, match.Value.Item.AudioKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端断连 / 请求取消：不得降级为中性继续调 TTS，须原样上抛交由框架处理。
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "training synthesize: emotion reference unavailable; degrade to neutral");
            return null;
        }
    }

    /// <summary>
    /// 单轮遍历音色候选：首个音频可取者为 speaker；<paramref name="captureDatasetEmotion"/> 为真时
    /// 同轮记录首个「音频可取且带情绪标注」的候选为 dataset 模式情绪来源。两者就绪（或仅需 speaker）即提前退出。
    /// </summary>
    private async Task<(ReferenceMatch? Speaker, ReferenceMatch? DatasetEmotion)> ScanCandidatesAsync(
        ObjectId datasetId, IReadOnlyList<VectorMatch> candidates, bool captureDatasetEmotion, CancellationToken ct)
    {
        ReferenceMatch? speaker = null;
        ReferenceMatch? datasetEmotion = null;

        foreach (var match in candidates)
        {
            var item = await items.GetByIdAsync(datasetId, match.ItemId, ct);
            if (item is null)
            {
                continue;
            }

            AudioClip clip;
            try
            {
                clip = await audio.GetClipAsync(item.AudioUri, item.StartMs, item.EndMs, ct);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with invalid audio uri itemId={ItemId}", match.ItemId);
                continue;
            }
            catch (AudioUnavailableException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with unavailable audio itemId={ItemId}", match.ItemId);
                continue;
            }
            catch (AudioTooLargeException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip oversized candidate audio itemId={ItemId}", match.ItemId);
                continue;
            }

            speaker ??= new ReferenceMatch(item, clip, match);

            // dataset 模式：命中条目自身无情绪标注时继续沿候选向下找。
            if (captureDatasetEmotion && datasetEmotion is null && ReadEmotion(item) is not null)
            {
                datasetEmotion = new ReferenceMatch(item, clip, match);
            }

            if (speaker is not null && (!captureDatasetEmotion || datasetEmotion is not null))
            {
                break;
            }
        }

        return (speaker, datasetEmotion);
    }

    private static EmotionResult? ReadEmotion(TrainingDatasetItemRecord record) =>
        record.Extensions is not null
        && record.Extensions.TryGetValue("emotion", out var raw)
        && raw.IsBsonDocument
            ? TrainingMapping.FromEmotionDocument(raw.AsBsonDocument)
            : null;

    private readonly record struct ReferenceMatch(
        TrainingDatasetItemRecord Item, AudioClip Clip, VectorMatch Match);

    private readonly record struct EmotionMatch(TtsEmotionReference Reference, string AudioKey);
}
