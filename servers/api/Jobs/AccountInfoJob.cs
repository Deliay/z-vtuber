using Z.Vtuber.Configuration;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Jobs;

public sealed class AccountInfoJob : PeriodicJobBase
{
    private readonly ISubscriptionService _subscription;
    private readonly IAccountInfoCrawler _accountInfoCrawler;
    private readonly BilibiliCrawlerOptions _options;

    public AccountInfoJob(
        ISubscriptionService subscription,
        IAccountInfoCrawler accountInfoCrawler,
        BilibiliCrawlerOptions options,
        ILogger<AccountInfoJob> logger) : base(logger)
    {
        _subscription = subscription;
        _accountInfoCrawler = accountInfoCrawler;
        _options = options;
        subscription.Subscribed += OnSubscribed;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(_options.AccountInfoIntervalHours);

    internal override async Task RunOnceAsync(CancellationToken ct)
    {
        // AccountInfoJob 需要处理所有类型的订阅（dynamic 和 danmaku）
        var dynamicMids = await _subscription.GetMidsAsync(SubscriptionType.Dynamic, ct);
        var danmakuMids = await _subscription.GetMidsAsync(SubscriptionType.Danmaku, ct);
        var allMids = dynamicMids.Concat(danmakuMids).Distinct().ToList();
        foreach (var mid in allMids)
        {
            await _accountInfoCrawler.CrawlAsync(mid, ct);
        }
    }

    private void OnSubscribed(long mid, SubscriptionType type) => _ = _accountInfoCrawler.CrawlAsync(mid);
}
