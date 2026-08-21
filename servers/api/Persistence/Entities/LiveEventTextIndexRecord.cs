using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Persistence.Entities;

public sealed class LiveEventTextIndexRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId EventId { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string SenderName { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Int64)]
    public long SenderUid { get; set; }

    public string SenderMedal { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; set; }
}
