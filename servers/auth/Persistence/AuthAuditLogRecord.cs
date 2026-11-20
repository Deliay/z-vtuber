using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

public sealed class AuthAuditLogRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string Event { get; set; } = string.Empty;

    public string? RequestId { get; set; }

    public string? SessionId { get; set; }

    public string? Platform { get; set; }

    public long? PlatformUid { get; set; }

    public string? SourceRoomId { get; set; }

    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; set; }

    public string? Detail { get; set; }
}
