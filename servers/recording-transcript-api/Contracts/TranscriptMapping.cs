using System.Text.Json.Nodes;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Api.Contracts;

/// <summary>实体到响应 DTO 的映射（模型 DTO 永不携带 ApiKey）。</summary>
internal static class TranscriptMapping
{
    public static RecordingItemDto ToRecordingItem(
        RecordingRecord record,
        string? anchorName = null,
        IReadOnlyList<string>? files = null,
        long? size = null) => new(
        record.Id.ToString(),
        record.RoomId,
        record.Mid,
        anchorName,
        record.Title,
        files ?? record.Files,
        size,
        record.StartedAt,
        record.EndedAt);

    public static TranscriptTaskDto ToTaskDto(
        TranscriptTaskRecord task, RecordingRecord? recording, TranscriptModelRecord? model) => new(
        task.Id.ToString(),
        task.RecordingId.ToString(),
        task.RoomId,
        recording?.Title,
        string.IsNullOrEmpty(task.File) ? null : task.File,
        task.ModelId.ToString(),
        model?.Name,
        task.Type,
        task.Mode,
        task.Source,
        task.Status,
        task.Position,
        task.OffsetMs,
        task.Error,
        task.CreatedAt,
        task.UpdatedAt);

    /// <summary>
    /// 片段 → DTO：在展示时间上叠加任务播放偏移。
    /// 负值不钳制（保持 start 单射，避免多句同钳 0 在 web 以 start 为键的合并中互相覆盖）；
    /// 钳 0 只发生在字幕渲染（既有）与前端试听区间。
    /// </summary>
    public static TranscriptSegmentDto ToSegmentDto(TranscriptSegmentRecord segment, long offsetMs = 0) =>
        new(segment.StartTime + offsetMs, segment.EndTime + offsetMs, segment.Text);

    public static TranscriptModelDto ToModelDto(TranscriptModelRecord model) => new(
        model.Id.ToString(),
        model.Name,
        model.Provider,
        model.Type,
        model.Model,
        model.Enabled,
        model.MaxConcurrency,
        model.CreatedAt);

    public static string? ExtractAnchorName(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(data) is not JsonObject obj)
            {
                return null;
            }

            return obj["uName"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
        }
        catch
        {
            return null;
        }
    }
}
