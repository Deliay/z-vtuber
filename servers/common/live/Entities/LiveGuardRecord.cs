using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class LiveGuardRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long Mid { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long RoomId { get; set; }

    public string Date { get; set; } = string.Empty;

    public List<LiveGuardEntry> Guards { get; set; } = [];

    public int GuardCount { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FetchedAt { get; set; }
}

public sealed class LiveGuardEntry
{
    [BsonRepresentation(BsonType.Int64)]
    public long Uid { get; set; }

    public string UserName { get; set; } = string.Empty;

    public int GuardLevel { get; set; }

    public string? MedalName { get; set; }

    public int MedalLevel { get; set; }
}