using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public static class TranscriptTaskStatus
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
}

public static class TranscriptTaskModes
{
    public const string Live = "live";
    public const string File = "file";
}

public static class TranscriptTaskSources
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

public static class TranscriptTaskTypes
{
    public const string Recording = "recording";
}

/// <summary>
/// 标注 [BsonIgnoreExtraElements]：容忍存量/异构版本文档中的未映射元素（如旧二进制读到新字段 OffsetMs
/// 或任何前向残留字段），避免 BsonClassMapSerializer 对未映射元素抛 FormatException。
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TranscriptTaskRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId RecordingId { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    public string File { get; set; } = string.Empty;

    public ObjectId ModelId { get; set; }

    public string Type { get; set; } = TranscriptTaskTypes.Recording;

    public string Mode { get; set; } = TranscriptTaskModes.File;

    public string Source { get; set; } = TranscriptTaskSources.Manual;

    public long Position { get; set; }

    /// <summary>任务播放偏移（毫秒，可正可负，默认 0）；详情/SSE/字幕三出口在展示时间上叠加该偏移。</summary>
    public long OffsetMs { get; set; }

    public string Status { get; set; } = TranscriptTaskStatus.Pending;

    public string? Error { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
