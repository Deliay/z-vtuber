namespace Z.Vtuber.Cookie.Pool;

public interface ICookieDispatchService
{
    Task<(string CookieId, string Cookie)> CheckoutAsync(CancellationToken ct = default);
    Task SetCooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default);
}

public class CookieDispatchService : ICookieDispatchService
{
    public Task<(string CookieId, string Cookie)> CheckoutAsync(CancellationToken ct = default)
    {
        // TODO: implement checkout
        return Task.FromResult(("fake-id", "fake-cookie"));
    }

    public Task SetCooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        // TODO: implement cooldown
        return Task.CompletedTask;
    }
}