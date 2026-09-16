using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class TranscriptAutoEnqueueRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Mid { get; set; }

    public bool Enabled { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
