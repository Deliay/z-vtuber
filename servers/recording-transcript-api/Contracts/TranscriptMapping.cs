using System.Text.Json.Nodes;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Api.Contracts;

/// <summary>实体到响应 DTO 的映射（模型 DTO 永不携带 ApiKey）。</summary>
internal static class TranscriptMapping
{
    public static RecordingItemDto ToRecordingItem(RecordingRecord record) => new(
        record.Id.ToString(),
        record.RoomId,
        record.Mid,
        record.Title,
        record.Files,
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
        task.Error,
        task.CreatedAt,
        task.UpdatedAt);

    public static TranscriptSegmentDto ToSegmentDto(TranscriptSegmentRecord segment) =>
        new(segment.StartTime, segment.EndTime, segment.Text);

    public static TranscriptModelDto ToModelDto(TranscriptModelRecord model) => new(
        model.Id.ToString(),
        model.Name,
        model.Provider,
        model.Type,
        model.Enabled,
        model.IsDefault,
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
