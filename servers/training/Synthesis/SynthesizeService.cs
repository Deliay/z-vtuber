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

/// <summary>合成结果：音频字节 + 命中的音色（按拼接顺序的 audioKey 序列）/情绪参考 audioKey（可观测）。</summary>
public sealed record SynthesizeResult(
    byte[] Audio, IReadOnlyList<string> SpeakerAudioKeys, string? EmoAudioKey);

/// <summary>数据集内无任何可用音色参考（无带文本的条目）→ 映射 409 <c>no speaker reference</c>。</summary>
public sealed class NoSpeakerReferenceException() : Exception("no speaker reference");

/// <summary>
/// 音频生成编排：音色参考优先「断句 + 拼音相似度多命中 + 多音频拼接」——输入文本按 <c>[，。,.\s]</c> 断句后，
/// 每条短句与数据集候选短句（<c>training_dataset_item_segments</c>）按音节级归一化编辑距离取最相似 1 条，
/// 命中按数据 id 去重（输入句序优先、同分 itemId/SegmentIndex 升序）后按条数上限（<see cref="SynthesizeOptions.MergedReferenceLimit"/>）
/// 与总时长上限（<see cref="SynthesizeOptions.MergedReferenceMaxTotalDurationMs"/>，与 recording 侧同口径）截断，
/// 再经 recording 合并为拼接音源；无候选短句时回退既有字符相似单条路径。
/// 情绪参考按 <see cref="EmotionMode"/> 取值——<see cref="EmotionMode.None"/>（默认）不传、
/// <see cref="EmotionMode.Llm"/> 用可用模型生成情感向量后经 Milvus 检索 top-1、
/// <see cref="EmotionMode.Dataset"/> 沿音色命中条目取首个带 <c>Extensions.emotion</c> 者为参考（best-effort → 中性），
/// 调上游 TTS 返回 WAV。日志不打文本/音频内容。
/// </summary>
public sealed class SynthesizeService(
    TrainingDatasetItemRepository items,
    TrainingDatasetItemSegmentRepository segments,
    TrainingProviderRepository providers,
    IChatModelClientFactory aiFactory,
    IRecordingAudioClient audio,
    IVectorStore store,
    ITtsClient tts,
    SynthesizeOptions synthesizeOptions,
    AiOptions aiOptions,
    ILogger<SynthesizeService> logger)
{
    public async Task<SynthesizeResult> SynthesizeAsync(
        ObjectId datasetId, string text, string lang, TrainingModelRecord? emotionModel,
        EmotionMode emotionMode = EmotionMode.None, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1) 音色参考：拼音短句多命中拼接优先；无候选回退字符相似单条。
        var speaker = await ResolveSpeakerAsync(datasetId, text, emotionMode, ct);

        // 2) 情绪参考：llm 用大模型生成查询向量后经 Milvus 检索；dataset 沿音色命中条目取自身 Extensions.emotion。
        var emotion = emotionMode == EmotionMode.None
            ? (EmotionMatch?)null
            : emotionMode == EmotionMode.Dataset
                ? speaker.DatasetEmotion ?? await TryCaptureDatasetEmotionAsync(speaker.Items, ct)
                : await TryBuildEmotionReferenceAsync(datasetId, text, emotionModel, ct);

        // 3) 上游合成。
        var request = new TtsSynthesizeRequest(text, lang, speaker.Reference, emotion?.Reference);
        var bytes = await tts.SynthesizeAsync(request, ct);

        var speakerKeys = speaker.AudioKeys;
        var emoKey = emotion?.AudioKey;
        stopwatch.Stop();
        logger.LogInformation(
            "training synthesize: datasetId={DatasetId} speakerKeys={SpeakerKeys} emoKey={EmoKey} lang={Lang} emotionMode={EmotionMode} elapsedMs={ElapsedMs}",
            datasetId, speakerKeys.Count, emoKey, lang, emotionMode, stopwatch.ElapsedMilliseconds);

        return new SynthesizeResult(bytes, speakerKeys, emoKey);
    }

    /// <summary>音色参考解析：优先拼音短句路径，无候选短句时回退字符相似单条路径。</summary>
    private async Task<SpeakerOutcome> ResolveSpeakerAsync(
        ObjectId datasetId, string text, EmotionMode emotionMode, CancellationToken ct)
    {
        var pinyin = await TryResolvePinyinSpeakerAsync(datasetId, text, ct);
        return pinyin ?? await ResolveCharSpeakerAsync(datasetId, text, emotionMode, ct);
    }

    /// <summary>
    /// 拼音短句匹配：断句 → 各句拼音音节序列 → 对全部候选短句计算音节级相似度取最相似 1 条
    /// （同分 itemId 升序、SegmentIndex 升序）→ 按 ItemId 去重（输入句序优先）→ 回读条目 → 过滤不可解析音频
    /// → 按条数（<see cref="SynthesizeOptions.MergedReferenceLimit"/>）与总时长（<see cref="SynthesizeOptions.MergedReferenceMaxTotalDurationMs"/>）截断
    /// → 合并拼接。无候选/无命中返回 null（回退字符相似）。
    /// </summary>
    private async Task<SpeakerOutcome?> TryResolvePinyinSpeakerAsync(
        ObjectId datasetId, string text, CancellationToken ct)
    {
        var sentences = TextSplitter.Split(text);
        if (sentences.Count == 0)
        {
            return null;
        }

        List<TrainingDatasetItemSegmentRecord> candidates;
        try
        {
            candidates = await segments.ListByDatasetAsync(datasetId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "training synthesize: segment load failed; fall back to char similarity");
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // 每条输入短句分别取拼音最相似 1 条候选（同分 itemId 升序、SegmentIndex 升序稳定 tie-break）。
        var hits = new List<TrainingDatasetItemSegmentRecord>();
        foreach (var sentence in sentences)
        {
            var query = PinyinConverter.ToSyllables(sentence);
            if (query.Count == 0)
            {
                continue;
            }

            TrainingDatasetItemSegmentRecord? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var candidate in candidates)
            {
                var score = PinyinSimilarity.Ratio(query, PinyinConverter.Split(candidate.Pinyin));
                if (best is null || score > bestScore || (score == bestScore && IsBetterTieBreak(candidate, best)))
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best is not null)
            {
                hits.Add(best);
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        // 命中按数据 id 去重（保留首次命中序：输入短句顺序优先）。
        var seen = new HashSet<ObjectId>();
        var deduped = new List<TrainingDatasetItemSegmentRecord>();
        foreach (var hit in hits)
        {
            if (seen.Add(hit.ItemId))
            {
                deduped.Add(hit);
            }
        }

        // 回读条目（Mongo 事实源）→ 过滤音频不可解析者 → 按条数上限与总时长上限截断拼接。
        // 总时长与 recording 侧 AudioMergeMaxTotalDurationMs 同口径，避免合并请求被 400 后静默降级为单片段。
        var requests = new List<AudioClipRequest>(deduped.Count);
        var usable = new List<TrainingDatasetItemRecord>(deduped.Count);
        var totalDurationMs = 0L;
        foreach (var segment in deduped)
        {
            if (requests.Count >= synthesizeOptions.MergedReferenceLimit)
            {
                break;
            }

            var item = await items.GetByIdAsync(datasetId, segment.ItemId, ct);
            if (item is null)
            {
                continue;
            }

            var parsed = TrainingMapping.ParseAudioUri(item.AudioUri);
            if (parsed is null)
            {
                logger.LogWarning(
                    "training synthesize: skip candidate with invalid audio uri itemId={ItemId}", item.Id);
                continue;
            }

            // endMs 为空按 Ai:AudioClipDefaultDurationMs 兜底（与取数/合并客户端同口径）。
            var durationMs = (item.EndMs ?? item.StartMs + aiOptions.AudioClipDefaultDurationMs) - item.StartMs;
            if (durationMs <= 0)
            {
                continue;
            }

            if (totalDurationMs + durationMs > synthesizeOptions.MergedReferenceMaxTotalDurationMs)
            {
                break;
            }

            usable.Add(item);
            requests.Add(new AudioClipRequest(parsed.RoomId, parsed.File, item.StartMs, item.EndMs));
            totalDurationMs += durationMs;
        }

        if (requests.Count == 0)
        {
            return null;
        }

        return await MergeOrFallbackAsync(usable, requests, ct);
    }

    /// <summary>
    /// 合并拼接：<see cref="IRecordingAudioClient.MergeClipsAsync"/> 成功 → 拼接音源（多 audioKey）；
    /// 合并整体失败逐条单片段兜底（按序取首个可用）；全失败抛 <see cref="AudioUnavailableException"/>（502）。
    /// </summary>
    private async Task<SpeakerOutcome> MergeOrFallbackAsync(
        IReadOnlyList<TrainingDatasetItemRecord> usable,
        IReadOnlyList<AudioClipRequest> requests,
        CancellationToken ct)
    {
        try
        {
            var merged = await audio.MergeClipsAsync(requests, ct);
            return new SpeakerOutcome(
                new TtsSpeakerReference(merged.Bytes, merged.ContentType),
                usable.Select(i => i.AudioKey).ToList(),
                usable,
                null);
        }
        catch (AudioUnavailableException ex)
        {
            logger.LogWarning(
                ex, "training synthesize: merge failed; falling back to first available single clip");
        }

        foreach (var item in usable)
        {
            AudioClip clip;
            try
            {
                clip = await audio.GetClipAsync(item.AudioUri, item.StartMs, item.EndMs, ct);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with invalid audio uri itemId={ItemId}", item.Id);
                continue;
            }
            catch (AudioUnavailableException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with unavailable audio itemId={ItemId}", item.Id);
                continue;
            }
            catch (AudioTooLargeException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip oversized candidate audio itemId={ItemId}", item.Id);
                continue;
            }

            return new SpeakerOutcome(
                new TtsSpeakerReference(clip.Bytes, clip.ContentType),
                [item.AudioKey],
                [item],
                null);
        }

        throw new AudioUnavailableException("no speaker reference audio available");
    }

    /// <summary>
    /// 回退路径（无候选短句）：读数据集全部带文本条目，按输入文本与条目文本的字符相似度降序、
    /// 同分 itemId 升序稳定排序取前 <see cref="SynthesizeOptions.ReferenceCandidateLimit"/> 条逐候选兜底；
    /// dataset 模式同轮捕获首个带情绪标注的候选。
    /// </summary>
    private async Task<SpeakerOutcome> ResolveCharSpeakerAsync(
        ObjectId datasetId, string text, EmotionMode emotionMode, CancellationToken ct)
    {
        var candidates = await RankSpeakerCandidatesAsync(datasetId, text, ct);
        if (candidates.Count == 0)
        {
            throw new NoSpeakerReferenceException();
        }

        var captureDatasetEmotion = emotionMode == EmotionMode.Dataset;
        var (speaker, datasetEmotion) = await ScanCandidatesAsync(candidates, captureDatasetEmotion, ct);
        if (speaker is null)
        {
            throw new AudioUnavailableException("no speaker reference audio available");
        }

        return new SpeakerOutcome(
            new TtsSpeakerReference(speaker.Value.Clip.Bytes, speaker.Value.Clip.ContentType),
            [speaker.Value.Item.AudioKey],
            [],
            BuildDatasetEmotionReference(datasetEmotion));
    }

    /// <summary>
    /// dataset 模式（拼音路径）：沿音色命中条目顺序取首个带 <c>Extensions.emotion</c> 者为情绪参考；
    /// 取音频失败/全无标注 → null（降级中性，best-effort）。
    /// </summary>
    private async Task<EmotionMatch?> TryCaptureDatasetEmotionAsync(
        IReadOnlyList<TrainingDatasetItemRecord> candidates, CancellationToken ct)
    {
        foreach (var item in candidates)
        {
            var emotion = ReadEmotion(item);
            if (emotion is null)
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
                logger.LogWarning(ex, "training synthesize: skip emotion candidate with invalid audio itemId={ItemId}", item.Id);
                continue;
            }
            catch (AudioUnavailableException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip emotion candidate with unavailable audio itemId={ItemId}", item.Id);
                continue;
            }
            catch (AudioTooLargeException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip oversized emotion candidate audio itemId={ItemId}", item.Id);
                continue;
            }

            return new EmotionMatch(
                new TtsEmotionReference(clip.Bytes, clip.ContentType, emotion), item.AudioKey);
        }

        return null;
    }

    /// <summary>
    /// 字符相似候选排序：读数据集全部条目，过滤空文本，按输入文本与条目文本的归一化编辑距离相似度降序、
    /// 同分按 itemId 升序（稳定 tie-break），取前 <see cref="SynthesizeOptions.ReferenceCandidateLimit"/> 条。
    /// </summary>
    private async Task<IReadOnlyList<TrainingDatasetItemRecord>> RankSpeakerCandidatesAsync(
        ObjectId datasetId, string text, CancellationToken ct)
    {
        var records = await items.ListAsync(datasetId, ct: ct);
        return records
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => (Record: r, Score: TextSimilarity.Ratio(text, r.Text.Trim())))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Record.Id)
            .Take(synthesizeOptions.ReferenceCandidateLimit)
            .Select(x => x.Record)
            .ToList();
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

            // 情感候选（Milvus 输出）回到 Mongo 事实源取条目，再逐候选兜底音频。
            var records = new List<TrainingDatasetItemRecord>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var item = await items.GetByIdAsync(datasetId, candidate.ItemId, ct);
                if (item is not null)
                {
                    records.Add(item);
                }
            }

            var (match, _) = await ScanCandidatesAsync(records, captureDatasetEmotion: false, ct);
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
    /// 单轮遍历候选（已按匹配度有序）：首个音频可取者为 speaker；<paramref name="captureDatasetEmotion"/> 为真时
    /// 同轮记录首个「音频可取且带情绪标注」的候选为 dataset 模式情绪来源。两者就绪（或仅需 speaker）即提前退出。
    /// </summary>
    private async Task<(ReferenceMatch? Speaker, ReferenceMatch? DatasetEmotion)> ScanCandidatesAsync(
        IReadOnlyList<TrainingDatasetItemRecord> candidates, bool captureDatasetEmotion, CancellationToken ct)
    {
        ReferenceMatch? speaker = null;
        ReferenceMatch? datasetEmotion = null;

        foreach (var item in candidates)
        {
            AudioClip clip;
            try
            {
                clip = await audio.GetClipAsync(item.AudioUri, item.StartMs, item.EndMs, ct);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with invalid audio uri itemId={ItemId}", item.Id);
                continue;
            }
            catch (AudioUnavailableException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip candidate with unavailable audio itemId={ItemId}", item.Id);
                continue;
            }
            catch (AudioTooLargeException ex)
            {
                logger.LogWarning(ex, "training synthesize: skip oversized candidate audio itemId={ItemId}", item.Id);
                continue;
            }

            speaker ??= new ReferenceMatch(item, clip);

            // dataset 模式：命中条目自身无情绪标注时继续沿候选向下找。
            if (captureDatasetEmotion && datasetEmotion is null && ReadEmotion(item) is not null)
            {
                datasetEmotion = new ReferenceMatch(item, clip);
            }

            if (speaker is not null && (!captureDatasetEmotion || datasetEmotion is not null))
            {
                break;
            }
        }

        return (speaker, datasetEmotion);
    }

    internal static bool IsBetterTieBreak(
        TrainingDatasetItemSegmentRecord candidate, TrainingDatasetItemSegmentRecord current) =>
        candidate.ItemId < current.ItemId
        || (candidate.ItemId == current.ItemId && candidate.SegmentIndex < current.SegmentIndex);

    private static EmotionResult? ReadEmotion(TrainingDatasetItemRecord record) =>
        record.Extensions is not null
        && record.Extensions.TryGetValue("emotion", out var raw)
        && raw.IsBsonDocument
            ? TrainingMapping.FromEmotionDocument(raw.AsBsonDocument)
            : null;

    /// <summary>音色参考解析结果：拼接/单条音源、按序 audioKey、数据集情绪回退条目、同轮已捕获的 dataset 情绪。</summary>
    private sealed record SpeakerOutcome(
        TtsSpeakerReference Reference,
        IReadOnlyList<string> AudioKeys,
        IReadOnlyList<TrainingDatasetItemRecord> Items,
        EmotionMatch? DatasetEmotion);

    private readonly record struct ReferenceMatch(TrainingDatasetItemRecord Item, AudioClip Clip);

    private readonly record struct EmotionMatch(TtsEmotionReference Reference, string AudioKey);
}
