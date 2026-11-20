using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

/// <summary>standalone 模式的 opaque 本地会话（access/refresh 只存哈希）。</summary>
public sealed class AuthSessionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string FamilyId { get; set; } = string.Empty;

    public ObjectId UserId { get; set; }

    public string AccessTokenHash { get; set; } = string.Empty;

    public string RefreshTokenHash { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AccessExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime RefreshExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RevokedAt { get; set; }

    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }
}
