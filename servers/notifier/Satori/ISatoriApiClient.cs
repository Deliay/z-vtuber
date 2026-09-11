using System.Text.Json.Serialization;

namespace Z.Vtuber.Notifier.Satori;

public interface ISatoriApiClient
{
    Task<LoginInfo> LoginGetAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guild>> GetGuildListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetChannelListAsync(string guildId, CancellationToken ct = default);
    Task SendMessageAsync(string channelId, string content, CancellationToken ct = default);
    string? GetCachedPlatform();
    string? GetCachedSelfId();
}

public sealed class LoginInfo
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public UserInfo User { get; set; } = new();

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

public sealed class UserInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class Guild
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class Channel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
