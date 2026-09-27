using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Contracts;

/// <summary>
/// 导入映射与纯函数（Plan 2.3 / 偏差 1 落点）。
/// </summary>
public static class TrainingMapping
{
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
            record.CreatedAt,
            record.UpdatedAt);

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
