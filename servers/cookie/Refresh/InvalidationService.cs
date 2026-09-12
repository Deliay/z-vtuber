namespace Z.Vtuber.Cookie.Refresh;

public interface IInvalidationService
{
    Task ReportInvalidAsync(string cookieId, CancellationToken ct = default);
}

public class InvalidationService : IInvalidationService
{
    public Task ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        // TODO: implement invalidation service
        return Task.CompletedTask;
    }
}