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

    /// <summary>绑定的转写模型 ID；空值表示"待完善"，自动转写不生效。</summary>
    public ObjectId? ModelId { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
