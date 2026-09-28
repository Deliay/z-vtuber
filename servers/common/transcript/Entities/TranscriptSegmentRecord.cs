using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class TranscriptSegmentRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId TaskId { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long StartTime { get; set; }

    [BsonRepresentation(BsonType.Int64)]
    public long EndTime { get; set; }

    public string Text { get; set; } = string.Empty;
}
