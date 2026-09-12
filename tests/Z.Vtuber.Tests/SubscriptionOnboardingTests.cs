using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

public class SubscriptionOnboardingTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonElement AccountInfoFor(long mid) =>
        JsonDocument.Parse($@"{{""code"":200,""data"":{{""channel"":{{""uId"":{mid},""roomId"":123456,""uName"":""test""}},""lives"":[]}}}}").RootElement.Clone();

    [Fact]
    public async Task Subscribe_NewMid_TriggersImmediateAccountInfoCrawl()
    {
        var subscription = new SubscriptionService(fixture.Database);
        var bili = new FakeBiliApiClient { AccountInfoProvider = AccountInfoFor };
        var accountRepository = new AccountBasicInfoRepository(fixture.Database);
        var liveSessionRepository = new LiveSessionRepository(fixture.Database);
        var crawler = new AccountInfoCrawler(bili, accountRepository, liveSessionRepository, new CrawlLockProvider(new FakeDistributedLockProvider()), NullLogger<AccountInfoCrawler>.Instance);

        Task? crawlTask = null;
        subscription.Subscribed += (mid, type) => crawlTask = crawler.CrawlAsync(mid);

        await subscription.SubscribeAsync(1150976664, SubscriptionType.Dynamic);

        Assert.NotNull(crawlTask);
        await crawlTask;

        var latest = await accountRepository.GetLatestByMidAsync(1150976664);
        Assert.NotNull(latest);
        Assert.Equal(123456L, latest.RoomId);
    }

    [Fact]
    public async Task Subscribe_DuplicateMid_DoesNotTriggerCrawl()
    {
        var subscription = new SubscriptionService(fixture.Database);
        var crawls = 0;
        subscription.Subscribed += (_, _) => crawls++;

        await subscription.SubscribeAsync(1150976664, SubscriptionType.Dynamic);
        await subscription.SubscribeAsync(1150976664, SubscriptionType.Dynamic);

        Assert.Equal(1, crawls);
    }
}
