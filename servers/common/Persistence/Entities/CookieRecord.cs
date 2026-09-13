using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class CookieRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string Cookie { get; set; } = string.Empty;

    public string? AccountId { get; set; }

    public string Status { get; set; } = CookieStatus.Active;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastCheckedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastUsedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CooldownUntil { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastRefreshAt { get; set; }
}

public static class CookieStatus
{
    public const string Active = "active";
    public const string Offline = "offline";
}
