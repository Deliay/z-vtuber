using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Persistence.Entities;

public sealed class AccountBasicInfoRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Mid { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    public string Data { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FetchedAt { get; set; }
}
