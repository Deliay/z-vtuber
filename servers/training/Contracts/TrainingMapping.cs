using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Contracts;

/// <summary>
/// 导入映射与纯函数（Plan 2.3 / 偏差 1 落点）。
/// </summary>
public static class TrainingMapping
{
    /// <summary>情绪维度强度上限（happy/angry/... 八维固定 0~1）。</summary>
    public const double MaxEmotionDimension = 1d;

    /// <summary>
    /// 情绪控制权重上限（<c>emotionControlWeight</c> 允许超过 1，越界钳制到 0~10 —— 与
    /// <see cref="Z.Vtuber.Training.Ai.ModelJsonParser.MaxEmotionControlWeight"/>、契约描述一致）。
    /// </summary>
    public const double MaxEmotionControlWeight = 10d;

    /// <summary>情绪描述最大长度（字符数，超过截断并记 Warn）。</summary>
    public const int MaxEmotionDescriptionLength = 200;

    /// <summary>
    /// 音频标识：<c>transcript:{taskId}:{原始startMs}</c>（原始 start 不含播放偏移，
    /// 保证任务偏移变更后重复导入仍命中 (datasetId, audioKey) 唯一索引而被去重）。
    /// </summary>
    public static string GenerateAudioKey(string taskId, long rawStartMs) =>
        $"transcript:{taskId}:{rawStartMs}";

    /// <summary>file 为空/空白 → ""；否则 <c>b/live/{roomId}/recordings/{file}</c>。</summary>
    public static string BuildAudioUri(long roomId, string? file) =>
        string.IsNullOrWhiteSpace(file) ? string.Empty : $"b/live/{roomId}/recordings/{file}";

    /// <summary><c>startMs &lt; 0</c> 或 <c>(endMs.HasValue &amp;&amp; endMs &lt;= startMs)</c> → false。</summary>
    public static bool IsValidTimeRange(long startMs, long? endMs) =>
        startMs >= 0 && (!endMs.HasValue || endMs.Value > startMs);

    public static TrainingDatasetDto ToDatasetDto(TrainingDatasetRecord record) =>
        new(
            record.Id.ToString(),
            record.Name,
            record.Type,
            record.CreatedAt,
            record.UpdatedAt);

    public static TrainingDatasetItemDto ToItemDto(TrainingDatasetItemRecord record) =>
        new(
            record.Id.ToString(),
            record.DatasetId.ToString(),
            record.AudioType,
            record.Source,
            record.AudioUri,
            record.AudioKey,
            record.StartMs,
            record.EndMs,
            record.Text,
            ToJsonElement(record.Extensions),
            ReadEmotion(record.Extensions),
            record.CreatedAt,
            record.UpdatedAt);

    /// <summary>音频地址解析结果：<c>roomId</c> + 录播文件名。</summary>
    public sealed record ParsedAudioUri(long RoomId, string File);

    /// <summary>
    /// 解析音频地址 <c>b/live/{roomId}/recordings/{file}</c>（与前端 <c>trainingAudioClipUrl</c> 同口径）；
    /// 空/不可解析/roomId 非数字/file 为空、含 <c>/</c> 或为 <c>.</c>/<c>..</c> → null。
    /// </summary>
    public static ParsedAudioUri? ParseAudioUri(string? audioUri)
    {
        const string prefix = "b/live/";
        const string marker = "/recordings/";
        if (string.IsNullOrWhiteSpace(audioUri) || !audioUri.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = audioUri[prefix.Length..];
        var index = rest.IndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
        {
            return null;
        }

        var roomPart = rest[..index];
        var file = rest[(index + marker.Length)..];
        // roomId 收紧为纯 ASCII 数字：拒绝符号（-1/+1）、空白（" 1"）与非数字；file 为空、含 '/'、
        // 或为相对路径段 '.'/'..'（Uri 归一化会改写路由）也拒绝。
        if (roomPart.Length == 0
            || !roomPart.All(char.IsAsciiDigit)
            || !long.TryParse(roomPart, out var roomId)
            || string.IsNullOrEmpty(file)
            || file.Contains('/')
            || file is "." or "..")
        {
            return null;
        }

        return new ParsedAudioUri(roomId, file);
    }

    /// <summary>模型展示 slug：<c>{providerName}/{model}</c>。</summary>
    public static string BuildModelSlug(string providerName, string model) =>
        $"{providerName}/{model}";

    public static TrainingProviderDto ToProviderDto(TrainingProviderRecord record) =>
        new(
            record.Id.ToString(),
            record.Name,
            record.Type,
            record.Endpoint,
            !string.IsNullOrEmpty(record.ApiKey),
            record.CreatedAt,
            record.UpdatedAt);

    public static TrainingModelDto ToModelDto(TrainingModelRecord record, string providerName) =>
        new(
            record.Id.ToString(),
            record.ProviderId.ToString(),
            providerName,
            record.Model,
            BuildModelSlug(providerName, record.Model),
            record.Enabled,
            record.InputCapabilities,
            record.OutputCapabilities,
            record.CreatedAt,
            record.UpdatedAt);

    /// <summary>
    /// 功能点 DTO 映射：实时计算项（指定模型可用性/默认选择）由调用方传入，不落库。
    /// </summary>
    public static TrainingFeaturePointDto ToFeaturePointDto(
        TrainingFeaturePointRecord record,
        string? specifiedModelSlug,
        bool specifiedModelAvailable,
        TrainingModelRecord? defaultModel,
        string? defaultModelSlug) =>
        new(
            record.Slug,
            record.Name,
            record.InputCapabilities,
            record.OutputCapabilities,
            record.ModelId?.ToString(),
            specifiedModelSlug,
            specifiedModelAvailable,
            defaultModel?.Id.ToString(),
            defaultModelSlug,
            record.CreatedAt,
            record.UpdatedAt);

    public static TrainingEmotionTaskDto ToEmotionTaskDto(TrainingEmotionTaskRecord record) =>
        new(
            record.Id.ToString(),
            record.DatasetId.ToString(),
            record.ItemId.ToString(),
            record.AudioKey,
            record.ModelId.ToString(),
            record.ModelSlug,
            record.Status.ToLowerInvariant(),
            record.Attempts,
            record.Error,
            FromEmotionDocument(record.Emotion),
            record.CreatedAt,
            record.StartedAt,
            record.FinishedAt);

    public static TrainingEmotionTaskCountsDto ToEmotionCountsDto(Dictionary<string, long> counts) =>
        new(
            counts.GetValueOrDefault(TrainingEmotionTaskStatus.Pending),
            counts.GetValueOrDefault(TrainingEmotionTaskStatus.Running),
            counts.GetValueOrDefault(TrainingEmotionTaskStatus.Done),
            counts.GetValueOrDefault(TrainingEmotionTaskStatus.Failed));

    /// <summary>情绪结果 → BsonDocument（小驼峰字段；数值越界钳制并记 Warn，描述超长截断并记 Warn）。</summary>
    public static BsonDocument ToEmotionDocument(EmotionResult emotion, ILogger? logger = null) =>
        new()
        {
            { "emotionControlWeight", ClampWithWarning(emotion.EmotionControlWeight, 0, MaxEmotionControlWeight, "emotionControlWeight", logger) },
            { "emotionDescription", TruncateDescription(emotion.EmotionDescription, logger) },
            { "happy", ClampWithWarning(emotion.Happy, 0, MaxEmotionDimension, "happy", logger) },
            { "angry", ClampWithWarning(emotion.Angry, 0, MaxEmotionDimension, "angry", logger) },
            { "sad", ClampWithWarning(emotion.Sad, 0, MaxEmotionDimension, "sad", logger) },
            { "afraid", ClampWithWarning(emotion.Afraid, 0, MaxEmotionDimension, "afraid", logger) },
            { "disgusted", ClampWithWarning(emotion.Disgusted, 0, MaxEmotionDimension, "disgusted", logger) },
            { "melancholic", ClampWithWarning(emotion.Melancholic, 0, MaxEmotionDimension, "melancholic", logger) },
            { "surprised", ClampWithWarning(emotion.Surprised, 0, MaxEmotionDimension, "surprised", logger) },
            { "calm", ClampWithWarning(emotion.Calm, 0, MaxEmotionDimension, "calm", logger) },
        };

    /// <summary>
    /// BsonDocument → 情绪结果：缺失/格式不符（任一数值字段缺失或非数值）→ null 不抛；
    /// 八维钳 0~1、<c>emotionControlWeight</c> 钳 0~10、描述截断 ≤200（越界/截断记 Warn）。
    /// </summary>
    public static EmotionResult? FromEmotionDocument(BsonDocument? document, ILogger? logger = null)
    {
        if (document is null
            || !TryGetNumber(document, "emotionControlWeight", out var weight)
            || !TryGetNumber(document, "happy", out var happy)
            || !TryGetNumber(document, "angry", out var angry)
            || !TryGetNumber(document, "sad", out var sad)
            || !TryGetNumber(document, "afraid", out var afraid)
            || !TryGetNumber(document, "disgusted", out var disgusted)
            || !TryGetNumber(document, "melancholic", out var melancholic)
            || !TryGetNumber(document, "surprised", out var surprised)
            || !TryGetNumber(document, "calm", out var calm))
        {
            return null;
        }

        var description = document.TryGetValue("emotionDescription", out var raw) && raw.IsString
            ? raw.AsString
            : string.Empty;

        return new EmotionResult(
            ClampWithWarning(weight, 0, MaxEmotionControlWeight, "emotionControlWeight", logger),
            TruncateDescription(description, logger),
            ClampWithWarning(happy, 0, MaxEmotionDimension, "happy", logger),
            ClampWithWarning(angry, 0, MaxEmotionDimension, "angry", logger),
            ClampWithWarning(sad, 0, MaxEmotionDimension, "sad", logger),
            ClampWithWarning(afraid, 0, MaxEmotionDimension, "afraid", logger),
            ClampWithWarning(disgusted, 0, MaxEmotionDimension, "disgusted", logger),
            ClampWithWarning(melancholic, 0, MaxEmotionDimension, "melancholic", logger),
            ClampWithWarning(surprised, 0, MaxEmotionDimension, "surprised", logger),
            ClampWithWarning(calm, 0, MaxEmotionDimension, "calm", logger));
    }

    private static EmotionResult? ReadEmotion(BsonDocument? extensions) =>
        extensions is not null && extensions.TryGetValue("emotion", out var raw) && raw.IsBsonDocument
            ? FromEmotionDocument(raw.AsBsonDocument)
            : null;

    private static bool TryGetNumber(BsonDocument document, string key, out double value)
    {
        value = 0;
        if (!document.TryGetValue(key, out var raw))
        {
            return false;
        }

        switch (raw.BsonType)
        {
            case BsonType.Double:
                value = raw.AsDouble;
                return true;
            case BsonType.Int32:
                value = raw.AsInt32;
                return true;
            case BsonType.Int64:
                value = raw.AsInt64;
                return true;
            case BsonType.Decimal128:
                value = (double)raw.AsDecimal;
                return true;
            default:
                return false;
        }
    }

    /// <summary>越界钳制并记 Warn（NaN 亦按越界处理）。</summary>
    private static double ClampWithWarning(double value, double min, double max, string field, ILogger? logger)
    {
        if (double.IsNaN(value) || value < min || value > max)
        {
            var clamped = Clamp(value, min, max);
            logger?.LogWarning(
                "Training emotion field {Field} out of range ({Value}); clamped to {Clamped}",
                field, value, clamped);
            return clamped;
        }

        return value;
    }

    /// <summary>描述超长（&gt; 200 字符）截断并记 Warn。</summary>
    private static string TruncateDescription(string? description, ILogger? logger)
    {
        var value = description ?? string.Empty;
        if (value.Length <= MaxEmotionDescriptionLength)
        {
            return value;
        }

        logger?.LogWarning(
            "Training emotion description too long ({Length} chars); truncated to {Max}",
            value.Length, MaxEmotionDescriptionLength);
        return value[..MaxEmotionDescriptionLength];
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(Math.Max(value, min), max);


    /// <summary>
    /// 导入映射：<c>rawStart = startMs - offsetMs</c>；<c>AudioKey = GenerateAudioKey(taskId, rawStart)</c>；
    /// <c>StartMs/EndMs</c> 为返回值（含偏移展示口径）；<c>AudioUri = BuildAudioUri(roomId, file)</c>；
    /// <c>Source = taskId</c>；<c>AudioType = "transcript"</c>。
    /// </summary>
    public static TrainingDatasetItemRecord ToImportItem(
        ObjectId datasetId, string taskId, long roomId, string? file, long offsetMs,
        long startMs, long endMs, string text, DateTime now)
    {
        var rawStartMs = startMs - offsetMs;
        return new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = taskId,
            AudioUri = BuildAudioUri(roomId, file),
            AudioKey = GenerateAudioKey(taskId, rawStartMs),
            StartMs = startMs,
            EndMs = endMs,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static JsonElement? ToJsonElement(BsonDocument? extensions)
    {
        if (extensions is null)
        {
            return null;
        }

        var json = extensions.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
