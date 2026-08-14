using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Persistence.Entities;

public sealed class SpaceTimelineRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Mid { get; set; }

    public string DynamicId { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Int64)]
    public long? PubTs { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FetchedAt { get; set; }
}
