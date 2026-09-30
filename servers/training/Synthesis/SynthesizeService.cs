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
/// 可用模型生成情感向量检索 top-1 取 <c>emo_prompt</c>/<c>emo_vector</c>（best-effort，失败降级中性）、
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
        CancellationToken ct = default)
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

        // 3) 逐候选取 speaker 音频（失败取次优；全部失败 → audio unavailable）。
        var speaker = await FetchFirstAvailableAsync(datasetId, speakerCandidates, ct)
            ?? throw new AudioUnavailableException("no speaker reference audio available");

        // 4) 情感向量生成 + 情绪参考（best-effort：模型失败/无候选 → 降级中性）。
        var emotion = await TryBuildEmotionReferenceAsync(datasetId, text, emotionModel, ct);

        // 5) 上游合成。
        var request = new TtsSynthesizeRequest(
            text,
            lang,
            new TtsSpeakerReference(speaker.Clip.Bytes, speaker.Clip.ContentType),
            emotion?.Reference);
        var bytes = await tts.SynthesizeAsync(request, ct);

        // 命中头回显条目记录（Mongo 事实源）的 audioKey，而非 Milvus 检索输出字段：
        // 后者若为畸形值会让响应头赋值抛异常，在 TTS 已成功后落成 500。
        var speakerKey = speaker.Item.AudioKey;
        var emoKey = emotion?.AudioKey;
        stopwatch.Stop();
        logger.LogInformation(
            "training synthesize: datasetId={DatasetId} speakerKey={SpeakerKey} emoKey={EmoKey} lang={Lang} elapsedMs={ElapsedMs}",
            datasetId, speakerKey, emoKey, lang, stopwatch.ElapsedMilliseconds);

        return new SynthesizeResult(bytes, speakerKey, emoKey);
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
            var match = await FetchFirstAvailableAsync(datasetId, candidates, ct);
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

    private async Task<ReferenceMatch?> FetchFirstAvailableAsync(
        ObjectId datasetId, IReadOnlyList<VectorMatch> candidates, CancellationToken ct)
    {
        foreach (var match in candidates)
        {
            var item = await items.GetByIdAsync(datasetId, match.ItemId, ct);
            if (item is null)
            {
                continue;
            }

            try
            {
                var clip = await audio.GetClipAsync(item.AudioUri, item.StartMs, item.EndMs, ct);
                return new ReferenceMatch(item, clip, match);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with invalid audio uri itemId={ItemId}", match.ItemId);
            }
            catch (AudioUnavailableException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with unavailable audio itemId={ItemId}", match.ItemId);
            }
            catch (AudioTooLargeException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip oversized candidate audio itemId={ItemId}", match.ItemId);
            }
        }

        return null;
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
