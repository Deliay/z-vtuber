using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Crawler.Jobs;

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
        // AccountInfoJob 需要处理所有类型的订阅（dynamic、danmaku、guard 和 recording）
        var dynamicMids = await _subscription.GetMidsAsync(SubscriptionType.Dynamic, ct);
        var danmakuMids = await _subscription.GetMidsAsync(SubscriptionType.Danmaku, ct);
        var guardMids = await _subscription.GetMidsAsync(SubscriptionType.Guard, ct);
        var recordingMids = await _subscription.GetMidsAsync(SubscriptionType.Recording, ct);
        var allMids = dynamicMids.Concat(danmakuMids).Concat(guardMids).Concat(recordingMids).Distinct().ToList();
        foreach (var mid in allMids)
        {
            await _accountInfoCrawler.CrawlAsync(mid, ct);
        }
    }

    private void OnSubscribed(long mid, SubscriptionType type) => _ = _accountInfoCrawler.CrawlAsync(mid);
}
