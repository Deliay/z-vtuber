namespace Z.Vtuber.Cookie.Browser;

public interface IBrowserSessionManager
{
    Task<string> CreateLoginSessionAsync(CancellationToken ct = default);
    Task DeleteSessionAsync(string accountId, CancellationToken ct = default);
    Task RefreshSessionAsync(string accountId, CancellationToken ct = default);
}