namespace Z.Vtuber.Notifier.Satori;

public interface ISatoriApiClient
{
    Task<LoginInfo> LoginGetAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guild>> GetGuildListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetChannelListAsync(string guildId, CancellationToken ct = default);
    Task SendMessageAsync(string channelId, string content, CancellationToken ct = default);
}

public sealed class LoginInfo
{
    public string Platform { get; set; } = string.Empty;
    public UserInfo User { get; set; } = new();
    public string Status { get; set; } = string.Empty;
}

public sealed class UserInfo
{
    public string Id { get; set; } = string.Empty;
}

public sealed class Guild
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class Channel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
