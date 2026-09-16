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

    public string Status { get; set; } = TranscriptTaskStatus.Pending;

    public string? Error { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
