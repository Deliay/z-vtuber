using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Jobs;
using Z.Vtuber.Persistence;
using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class SubscriptionOnboardingTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonElement AccountInfoFor(long mid) =>
        JsonDocument.Parse($@"{{""mid"":{mid},""live_room"":{{""roomid"":123456}}}}").RootElement.Clone();

    [Fact]
    public async Task Subscribe_NewMid_TriggersImmediateAccountInfoCrawl()
    {
        var subscription = new SubscriptionService(fixture.Database);
        var bili = new FakeBiliApiClient { AccountInfoProvider = AccountInfoFor };
        var cookiePool = new FakeCookiePoolService();
        var repository = new AccountBasicInfoRepository(fixture.Database);
        var crawler = new AccountInfoCrawler(bili, cookiePool, repository, new CrawlLockProvider(), NullLogger<AccountInfoCrawler>.Instance);

        Task? crawlTask = null;
        subscription.Subscribed += mid => crawlTask = crawler.CrawlAsync(mid);

        await subscription.SubscribeAsync(1150976664);

        Assert.NotNull(crawlTask);
        await crawlTask;

        var latest = await repository.GetLatestByMidAsync(1150976664);
        Assert.NotNull(latest);
        Assert.Equal(123456L, latest.RoomId);
    }

    [Fact]
    public async Task Subscribe_DuplicateMid_DoesNotTriggerCrawl()
    {
        var subscription = new SubscriptionService(fixture.Database);
        var crawls = 0;
        subscription.Subscribed += _ => crawls++;

        await subscription.SubscribeAsync(1150976664);
        await subscription.SubscribeAsync(1150976664);

        Assert.Equal(1, crawls);
    }
}
