using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class LiveEventRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    public string? LiveSessionId { get; set; }

    public string Type { get; set; } = string.Empty;

    [BsonSerializer(typeof(StringTolerantBsonDocumentSerializer))]
    public BsonDocument Payload { get; set; } = new();

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Price { get; set; }
}
