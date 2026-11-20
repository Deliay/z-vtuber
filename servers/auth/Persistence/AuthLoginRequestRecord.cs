using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

public sealed class AuthLoginRequestRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string RequestId { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public string PollTokenHash { get; set; } = string.Empty;

    public string? ClientNonceHash { get; set; }

    public string? ClientId { get; set; }

    public string? ReturnTo { get; set; }

    public string Platform { get; set; } = "bilibili";

    public string Status { get; set; } = LoginStatus.Pending;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ConfirmExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? VerifiedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ConfirmStartedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ConsumedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CancelledAt { get; set; }

    public string? ConfirmNonce { get; set; }

    public string? SourceRoomId { get; set; }

    public long? SenderUid { get; set; }

    public string? SenderName { get; set; }

    public string? SenderAvatar { get; set; }

    public string? SenderMedal { get; set; }

    public string? FailureCode { get; set; }

    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }

    public int Version { get; set; }
}
