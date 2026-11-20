using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Auth.Persistence;

public sealed class AuthOAuthCodeRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string CodeHash { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    public ObjectId UserId { get; set; }

    public ObjectId? ProviderSessionId { get; set; }

    public string Scope { get; set; } = "openid profile";

    public string? CodeChallenge { get; set; }

    public string? CodeChallengeMethod { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ConsumedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }
}
