using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public enum SubscriptionType
{
    [BsonRepresentation(BsonType.String)]
    Dynamic = 0,

    [BsonRepresentation(BsonType.String)]
    Danmaku = 1,

    [BsonRepresentation(BsonType.String)]
    Guard = 2
}
