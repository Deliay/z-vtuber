using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public class CookieSessionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }
    public string Status { get; set; } = "pending_login";
    public string UserDataDir { get; set; } = string.Empty;
    public long? Mid { get; set; }
    public string? Uname { get; set; }
    public string? CookieId { get; set; }
    public string? Cookie { get; set; }
    public string? QrCodeImage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public DateTime LastRefreshAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int ConsecutiveRefreshFailures { get; set; }
}
