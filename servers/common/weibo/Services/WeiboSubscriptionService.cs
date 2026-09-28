using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Services;

public interface IWeiboSubscriptionService
{
    event Action<long>? Subscribed;
    Task<IReadOnlyList<long>> GetUidsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WeiboSubscriptionRecord>> GetAllAsync(CancellationToken ct = default);
    Task SubscribeAsync(long uid, CancellationToken ct = default);
    Task<bool> UnsubscribeAsync(long uid, CancellationToken ct = default);
}

public sealed class WeiboSubscriptionService(WeiboSubscriptionRepository repository) : IWeiboSubscriptionService
{
    public event Action<long>? Subscribed;

    public Task<IReadOnlyList<long>> GetUidsAsync(CancellationToken ct = default) =>
        repository.GetUidsAsync(ct);

    public Task<IReadOnlyList<WeiboSubscriptionRecord>> GetAllAsync(CancellationToken ct = default) =>
        repository.GetAllAsync(ct);

    public async Task SubscribeAsync(long uid, CancellationToken ct = default)
    {
        if (uid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uid), "uid must be positive.");
        }

        var inserted = await repository.UpsertAsync(uid, ct);
        if (inserted)
        {
            Subscribed?.Invoke(uid);
        }
    }

    public async Task<bool> UnsubscribeAsync(long uid, CancellationToken ct = default)
    {
        if (uid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uid), "uid must be positive.");
        }

        return await repository.DeleteAsync(uid, ct);
    }
}
