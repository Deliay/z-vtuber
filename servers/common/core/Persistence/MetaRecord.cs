namespace Z.Vtuber.Common.Persistence;

public sealed class MetaRecord
{
    [MongoDB.Bson.Serialization.Attributes.BsonId]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Migration status: "applied" (completed) or "running" (in progress by another instance).
    /// Only used by migrations that need distributed locking (e.g. BackfillLiveEventPayloadObjectAsync).
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Timestamp when the migration was started (used to detect stale "running" state).
    /// </summary>
    public DateTime? StartedAt { get; set; }
}
