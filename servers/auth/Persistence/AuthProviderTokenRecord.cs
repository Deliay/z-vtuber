using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

public sealed class AuthProviderTokenRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string AccessTokenHash { get; set; } = string.Empty;

    public string? RefreshTokenHash { get; set; }

    public ObjectId UserId { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string Scope { get; set; } = "openid profile";

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RefreshExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RevokedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }
}
