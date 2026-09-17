using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class TranscriptModelRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string Name { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>实际发给服务商的模型 ID（如 fun-asr-realtime）。</summary>
    public string Model { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public int MaxConcurrency { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }
}
