using Z.Vtuber.Configuration;
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

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await _subscription.GetMidsAsync(ct);
        foreach (var mid in mids)
        {
            await _accountInfoCrawler.CrawlAsync(mid, ct);
        }
    }

    private void OnSubscribed(long mid) => _ = _accountInfoCrawler.CrawlAsync(mid);
}
