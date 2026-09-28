using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class NotificationSubscriptionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("event_type")]
    public string EventType { get; set; } = string.Empty;

    [BsonElement("group_id")]
    public string GroupId { get; set; } = string.Empty;

    [BsonElement("group_name")]
    public string? GroupName { get; set; }

    [BsonElement("created_at")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }
}