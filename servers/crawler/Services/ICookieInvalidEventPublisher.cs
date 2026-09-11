namespace Z.Vtuber.Crawler.Services;

public interface ICookieInvalidEventPublisher
{
    Task PublishAsync(string cookieId, string reason, CancellationToken ct = default);
}
