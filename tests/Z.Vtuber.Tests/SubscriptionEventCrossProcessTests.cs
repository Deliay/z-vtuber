using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Live;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class SubscriptionEventCrossProcessTests(MongoDbFixture mongo, RedisFixture redis)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static RedisChannel Channel =>
        new(SubscriptionEventChannels.Subscribed, RedisChannel.PatternMode.Literal);

    private sealed class FuncAccountInfoCrawler(Func<long, CancellationToken, Task> crawl) : IAccountInfoCrawler
    {
        public Task CrawlAsync(long mid, CancellationToken ct = default) => crawl(mid, ct);
    }

    /// <summary>
    /// 与 api Program 相同的装配方式启动宿主，验证 HostedService 注册会真正实例化 publisher 并挂载事件监听。
    /// </summary>
    [Fact]
    public async Task HostStartup_ActivatesPublisher_AndPublishesOnNewSubscription()
    {
        var apiSubscription = new SubscriptionService(mongo.Database);
        var received = new TaskCompletionSource<SubscriptionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = redis.Connection.GetSubscriber();
        await subscriber.SubscribeAsync(Channel, (_, value) =>
        {
            var evt = JsonSerializer.Deserialize<SubscriptionEvent>(value.ToString());
            if (evt is not null)
            {
                received.TrySetResult(evt);
            }
        });

        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<ISubscriptionService>(apiSubscription);
                services.AddSingleton<IConnectionMultiplexer>(redis.Connection);
                services.AddSubscriptionEvents();
            })
            .Build();

        try
        {
            // 断言注册为 HostedService（而非惰性单例），保证宿主启动即激活
            Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is SubscriptionEventPublisher);

            await host.StartAsync();

            await apiSubscription.SubscribeAsync(400001, SubscriptionType.Recording);

            var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(400001L, evt.Mid);
            Assert.Equal(SubscriptionType.Recording, evt.Type);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            await subscriber.UnsubscribeAsync(Channel);
        }
    }

    [Fact]
    public async Task CrossProcessSubscription_TriggersAccountCrawl_ThenRecordingStarts()
    {
        // 模拟 api 进程与 crawler 进程各自持有独立的 SubscriptionService 单例
        var apiSubscription = new SubscriptionService(mongo.Database);
        var crawlerSubscription = new SubscriptionService(mongo.Database);
        var accountRepository = new AccountBasicInfoRepository(mongo.Database);

        // 仅订阅录播的新 mid 初始无资料：roomId 不可解析
        Assert.Null(await accountRepository.GetLatestRoomIdByMidAsync(410001));

        // crawler 侧：订阅事件桥接 + AccountInfoJob（资料落库含 roomId）
        var crawled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var crawler = new FuncAccountInfoCrawler(async (mid, ct) =>
        {
            await accountRepository.UpsertAsync(new AccountBasicInfoRecord
            {
                Mid = mid,
                RoomId = 920001,
                Data = """{"uName":"新主播"}""",
                FetchedAt = DateTime.UtcNow,
            }, ct);
            crawled.TrySetResult(mid);
        });
        _ = new AccountInfoJob(crawlerSubscription, crawler, new BilibiliCrawlerOptions(), NullLogger<AccountInfoJob>.Instance);

        var eventSubscriber = new SubscriptionEventSubscriber(
            redis.Connection, crawlerSubscription, NullLogger<SubscriptionEventSubscriber>.Instance);
        await eventSubscriber.StartAsync(CancellationToken.None);

        var runner = new FakeRecordingSessionRunner();
        var poller = new LiveStatusPoller(
            crawlerSubscription,
            accountRepository,
            new FakeBiliLiveApi(),
            new FakeRecordingCookieClient(),
            runner,
            new RecordingStatusReporter(new RecordingStatusRepository(mongo.Database)),
            new RecordingOptions { PollIntervalSeconds = 1 },
            NullLogger<LiveStatusPoller>.Instance);

        try
        {
            // api 进程创建录播订阅
            await apiSubscription.SubscribeAsync(410001, SubscriptionType.Recording);

            // 容忍订阅通道建立延迟：重试发布直到 crawl 触发
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !crawled.Task.IsCompleted)
            {
                await redis.Connection.GetSubscriber().PublishAsync(
                    Channel,
                    JsonSerializer.Serialize(new SubscriptionEvent(410001, SubscriptionType.Recording)));
                await Task.Delay(200);
            }

            Assert.True(crawled.Task.IsCompleted, "跨进程订阅事件未触发 crawler 即时抓取");
            Assert.Equal(410001L, await crawled.Task);

            // 一个轮询周期内解析 roomId 并启动录制
            await poller.RunOnceAsync(CancellationToken.None);
            Assert.Contains(920001L, runner.StartedRooms);
        }
        finally
        {
            await eventSubscriber.StopAsync(CancellationToken.None);
            eventSubscriber.Dispose();
        }
    }
}
