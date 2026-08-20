using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Persistence.Entities;

public enum SubscriptionType
{
    [BsonRepresentation(BsonType.String)]
    Dynamic = 0,

    [BsonRepresentation(BsonType.String)]
    Danmaku = 1
}
