namespace Z.Vtuber.Cookie.Browser;

public class BrowserSessionManager : IBrowserSessionManager
{
    public Task<string> CreateLoginSessionAsync(CancellationToken ct = default)
    {
        // TODO: implement Playwright login session
        return Task.FromResult(Guid.NewGuid().ToString("N"));
    }

    public Task DeleteSessionAsync(string accountId, CancellationToken ct = default)
    {
        // TODO: implement session deletion
        return Task.CompletedTask;
    }

    public Task RefreshSessionAsync(string accountId, CancellationToken ct = default)
    {
        // TODO: implement session refresh
        return Task.CompletedTask;
    }
}