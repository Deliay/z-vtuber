using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Persistence.Entities;

public sealed class SubscriptionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Mid { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime SubscribedAt { get; set; }
}
