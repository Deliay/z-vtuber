using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

public sealed class AuthProviderSessionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string SessionIdHash { get; set; } = string.Empty;

    public ObjectId UserId { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime LastSeenAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RevokedAt { get; set; }

    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }
}
