namespace Z.Vtuber.Notifier.Milky;

public interface IMilkyApiClient
{
    Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GroupInfo>> GetGroupListAsync(CancellationToken ct = default);
    Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default);
}

public sealed class LoginInfo
{
    public long UserId { get; set; }
    public string? Nickname { get; set; }
}

public sealed class GroupInfo
{
    public long GroupId { get; set; }
    public string? GroupName { get; set; }
}