namespace Z.Vtuber.Cookie.Browser;

public interface IBrowserSessionManager
{
    Task<LoginSessionResult> CreateLoginSessionAsync(CancellationToken ct = default);
    Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default);
    Task<string> ExtractCookiesAsync(string accountId, CancellationToken ct = default);
    Task DeleteSessionAsync(string accountId, CancellationToken ct = default);
    Task RefreshSessionAsync(string accountId, CancellationToken ct = default);
}
