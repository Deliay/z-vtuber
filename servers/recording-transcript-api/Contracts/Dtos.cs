using System.Text.Json.Serialization;

namespace Z.Vtuber.RecordingTranscript.Api.Contracts;

public sealed record ErrorResponse([property: JsonPropertyName("error")] string Error);

public sealed record EnqueueRequest(
    [property: JsonPropertyName("recordingId")] string? RecordingId,
    [property: JsonPropertyName("modelId")] string? ModelId);

public sealed record EnqueueResponseDto(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("status")] string Status);

public sealed record RecordingItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("mid")] long Mid,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("endedAt")] DateTime? EndedAt);

public sealed record RoomTranscriptTaskRefDto(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("modelName")] string? ModelName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("position")] long Position,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

public sealed record RoomTranscriptRecordingDto(
    [property: JsonPropertyName("recordingId")] string RecordingId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("endedAt")] DateTime? EndedAt,
    [property: JsonPropertyName("tasks")] IReadOnlyList<RoomTranscriptTaskRefDto> Tasks);

public sealed record RoomTranscriptViewDto(
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("autoEnqueueEnabled")] bool AutoEnqueueEnabled,
    [property: JsonPropertyName("items")] IReadOnlyList<RoomTranscriptRecordingDto> Items);

public sealed record TranscriptSegmentDto(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End,
    [property: JsonPropertyName("text")] string Text);

public sealed record TranscriptTaskDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("recordingId")] string RecordingId,
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("modelName")] string? ModelName,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("position")] long Position,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

public sealed record TranscriptTaskDetailDto(
    [property: JsonPropertyName("task")] TranscriptTaskDto Task,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegmentDto> Segments);

public sealed record TranscriptSettingsItemDto(
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("mid")] long? Mid,
    [property: JsonPropertyName("anchorName")] string? AnchorName,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record TranscriptSettingsUpdateRequest(
    [property: JsonPropertyName("enable")] bool Enable);

public sealed record TranscriptSettingsUpdateResponse(
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record TranscriptModelDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("maxConcurrency")] int MaxConcurrency,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

public sealed record TranscriptModelCreateRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("isDefault")] bool? IsDefault,
    [property: JsonPropertyName("maxConcurrency")] int? MaxConcurrency);

public sealed record TranscriptModelUpdateRequest(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("isDefault")] bool? IsDefault,
    [property: JsonPropertyName("maxConcurrency")] int? MaxConcurrency,
    [property: JsonPropertyName("apiKey")] string? ApiKey);
