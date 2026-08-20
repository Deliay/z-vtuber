namespace Z.Vtuber.Live;

public sealed record LiveEvent(long RoomId, string Type, string? LiveSessionId, DateTime OccurredAt, string Payload, long? Price = null);
