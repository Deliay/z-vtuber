using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class LiveSessionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string LiveId { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? CoverUrl { get; set; }

    public string? ParentArea { get; set; }

    public string? Area { get; set; }

    public bool IsFinish { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime StartDate { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? StopDate { get; set; }

    public long DanmakuCount { get; set; }

    public double TotalIncome { get; set; }

    public long WatchCount { get; set; }

    public long LikeCount { get; set; }

    public long PayCount { get; set; }

    public long MaxOnlineCount { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FetchedAt { get; set; }
}
