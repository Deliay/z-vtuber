using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class CookieAccountInfoRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId CookieId { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Mid { get; set; }

    public string? Uname { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FetchedAt { get; set; }
}
