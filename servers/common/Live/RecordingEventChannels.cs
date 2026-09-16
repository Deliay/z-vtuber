using System.Text.Json.Serialization;

namespace Z.Vtuber.Common.Live;

/// <summary>
/// 录播转写事件频道约定：recording 服务发布 recording-started / recording-finished，
/// transcript-consumer 订阅；transcript-consumer 发布 transcript-segment:{taskId}，transcript-api SSE 订阅。
/// </summary>
public static class RecordingEventChannels
{
    public const string Started = "recording-started";
    public const string Finished = "recording-finished";

    public static string SegmentChannel(string taskId) => $"transcript-segment:{taskId}";
}

public sealed record RecordingStartedEvent(
    [property: JsonPropertyName("recordingId")] string RecordingId,
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("mid")] long Mid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("liveAudioUrl")] string LiveAudioUrl,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt);

public sealed record RecordingFinishedEvent(
    [property: JsonPropertyName("recordingId")] string RecordingId,
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("mid")] long Mid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("files")] string[] Files,
    [property: JsonPropertyName("fileUrls")] string[] FileUrls,
    [property: JsonPropertyName("endedAt")] DateTime EndedAt);

public sealed record TranscriptSegmentEvent(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End,
    [property: JsonPropertyName("text")] string Text);
