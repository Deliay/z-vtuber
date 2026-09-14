using System.Text.Json.Serialization;

namespace Z.Vtuber.Common.Live;

public sealed record CookieInvalidEvent
{
    [JsonPropertyName("cookie_id")]
    public required string CookieId { get; init; }

    [JsonPropertyName("mid")]
    public long? Mid { get; init; }

    [JsonPropertyName("uname")]
    public string? Uname { get; init; }

    [JsonPropertyName("occurred_at")]
    public required DateTime OccurredAt { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }
}
