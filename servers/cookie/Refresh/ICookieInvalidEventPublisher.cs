namespace Z.Vtuber.Cookie.Refresh;

public interface ICookieInvalidEventPublisher
{
    Task PublishAsync(string cookieId, string reason, CancellationToken ct = default);
}
