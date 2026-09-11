using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class NotificationSubscriptionRecord
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("event_type")]
    public string EventType { get; set; } = string.Empty;

    [BsonElement("platform")]
    public string Platform { get; set; } = string.Empty;

    [BsonElement("guild_id")]
    public string GuildId { get; set; } = string.Empty;

    [BsonElement("guild_name")]
    public string? GuildName { get; set; }

    [BsonElement("channel_id")]
    public string ChannelId { get; set; } = string.Empty;

    [BsonElement("channel_name")]
    public string? ChannelName { get; set; }

    [BsonElement("created_at")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }
}
