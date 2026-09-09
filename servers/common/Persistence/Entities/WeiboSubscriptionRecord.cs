using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class WeiboSubscriptionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Uid { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime SubscribedAt { get; set; }
}
