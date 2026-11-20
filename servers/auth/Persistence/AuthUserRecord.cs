using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

public sealed class AuthUserRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string Platform { get; set; } = "bilibili";

    [BsonRepresentation(BsonType.Int64)]
    public long PlatformUid { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public string Status { get; set; } = AuthUserStatus.Active;

    public string ProviderSubject { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime LastLoginAt { get; set; }

    public string? LastLoginIp { get; set; }

    public string? LastSourceRoomId { get; set; }

    public long LoginCount { get; set; }
}
