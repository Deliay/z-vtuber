using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class WeiboTimelineRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Uid { get; set; }

    public string MblogId { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Int64)]
    public long? PubTs { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FetchedAt { get; set; }
}
