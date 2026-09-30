using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

public class LiveGuardJobTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const long Mid = 1001;
    private const long RoomId = 123456;

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
        public override long TimestampFrequency => 1_000_000;
    }

    private static DateTime BeijingDateTime(int year, int month, int day, int hour) =>
        new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-8);

    private sealed class FakeLiveGuardListFetcher : LiveGuardListFetcher
    {
        public Func<string, string, long, long, CancellationToken, Task<List<LiveGuardEntry>?>>? FetchFunc { get; set; }

        public FakeLiveGuardListFetcher()
            : base(new FakeBiliApiClient(), new BilibiliCrawlerOptions(), NullLogger<LiveGuardListFetcher>.Instance)
        {
        }

        internal override Task<List<LiveGuardEntry>?> FetchAllAsync(string cookieId, string cookie, long mid, long roomId, CancellationToken ct)
            => FetchFunc?.Invoke(cookieId, cookie, mid, roomId, ct) ?? Task.FromResult<List<LiveGuardEntry>?>([]);
    }

    private BilibiliCrawlerOptions GuardOptions() => new()
    {
        GuardDailyCrawlHour = 23,
        GuardPageDelayMinSeconds = 1,
        GuardPageDelayMaxSeconds = 1,
        GuardMaxPages = 50,
    };

    private async Task SeedSubscriptionAsync(SubscriptionType type)
    {
        var subscriptions = fixture.Database.GetCollection<SubscriptionRecord>(LiveCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(new SubscriptionRecord
        {
            Mid = Mid,
            Type = type,
            SubscribedAt = DateTime.UtcNow,
        });
    }

    private async Task SeedAccountAsync(long roomId)
    {
        var accounts = fixture.Database.GetCollection<AccountBasicInfoRecord>(LiveCollectionNames.AccountBasicInfo);
        await accounts.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = Mid,
            RoomId = roomId,
            Data = "{}",
            FetchedAt = DateTime.UtcNow,
        });
    }

    private LiveGuardJob CreateJob(
        FakeLiveGuardListFetcher fetcher,
        DateTime? utcNow = null,
        AccountBasicInfoRepository? accountRepo = null,
        BilibiliCrawlerOptions? options = null,
        ICookieServiceClient? cookieServiceClient = null,
        ISubscriptionService? subscription = null,
        FakeTimeProvider? timeProvider = null)
    {
        accountRepo ??= new AccountBasicInfoRepository(fixture.Database);
        var accountInfoCrawler = new AccountInfoCrawler(
            new FakeBiliApiClient(),
            accountRepo,
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            NullLogger<AccountInfoCrawler>.Instance);

        return new LiveGuardJob(
            subscription ?? new SubscriptionService(fixture.Database),
            cookieServiceClient ?? new FakeCookieServiceClient(),
            accountRepo,
            new LiveGuardRepository(fixture.Database),
            accountInfoCrawler,
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            options ?? GuardOptions(),
            timeProvider ?? new FakeTimeProvider(utcNow ?? BeijingDateTime(2026, 8, 31, 23)),
            NullLogger<LiveGuardJob>.Instance,
            fetcher);
    }

    private static Task WaitForAsync(Func<Task<bool>> condition)
        => TestWait.UntilAsync(condition, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Run_BeforeDailyHour_DoesNotCrawl()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => throw new Exception("should not be called")
        };
        var job = CreateJob(fetcher, utcNow: BeijingDateTime(2026, 8, 31, 22));

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Run_AfterDailyHour_SnapshotsPendingMid()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => Task.FromResult<List<LiveGuardEntry>?>([
                new LiveGuardEntry { Uid = 1, UserName = "a", GuardLevel = 3, MedalName = "星", MedalLevel = 5 },
            ])
        };
        var job = CreateJob(fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(LiveCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal("2026-08-31", stored.Date);
        Assert.Equal(RoomId, stored.RoomId);
        Assert.Single(stored.Guards);
        Assert.Equal(1, stored.GuardCount);
        Assert.Equal(1L, stored.Guards[0].Uid);
        Assert.Equal("a", stored.Guards[0].UserName);
        Assert.Equal(3, stored.Guards[0].GuardLevel);
        Assert.Equal("星", stored.Guards[0].MedalName);
        Assert.Equal(5, stored.Guards[0].MedalLevel);
    }

    [Fact]
    public async Task Run_AlreadySnapshottedMid_IsNotRecrawled()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var guardRepo = new LiveGuardRepository(fixture.Database);
        await guardRepo.UpsertDailySnapshotAsync(new LiveGuardRecord
        {
            Mid = Mid,
            RoomId = RoomId,
            Date = "2026-08-31",
            Guards = [],
            GuardCount = 0,
            FetchedAt = DateTime.UtcNow,
        });

        var fetchCalls = 0;
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) =>
            {
                fetchCalls++;
                return Task.FromResult<List<LiveGuardEntry>?>([]);
            }
        };
        var job = CreateJob(fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, fetchCalls);
    }

    [Fact]
    public async Task Run_EmptyDiff_MakesNoRequests()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var guardRepo = new LiveGuardRepository(fixture.Database);
        await guardRepo.UpsertDailySnapshotAsync(new LiveGuardRecord { Mid = Mid, RoomId = RoomId, Date = "2026-08-31", Guards = [], GuardCount = 0, FetchedAt = DateTime.UtcNow });

        var fetchCalls = 0;
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) =>
            {
                fetchCalls++;
                return Task.FromResult<List<LiveGuardEntry>?>([]);
            }
        };
        var job = CreateJob(fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, fetchCalls);
    }

    [Fact]
    public async Task OnSubscribed_NonGuard_DoesNotTrigger()
    {
        await SeedSubscriptionAsync(SubscriptionType.Danmaku);
        await SeedAccountAsync(RoomId);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => throw new Exception("should not be called")
        };
        var subscription = new SubscriptionService(fixture.Database);
        var job = CreateJob(fetcher, subscription: subscription);

        await subscription.SubscribeAsync(Mid, SubscriptionType.Danmaku);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        // 非 Guard 订阅不得触发补抓：观察窗内轮询确认始终无快照。
        await TestWait.NeverAsync(
            async () => (await guardRepo.GetMidsWithSnapshotAsync("2026-08-31")).Count > 0,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(25),
            "non-guard subscription must not crawl");
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task OnSubscribed_BeforeCatchUpHour_CrawlsAsYesterdaySnapshot_AndDailyRunStillWorks()
    {
        await SeedAccountAsync(RoomId);
        var calls = 0;
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult<List<LiveGuardEntry>?>([
                    new LiveGuardEntry { Uid = 1 },
                ]);
            }
        };
        var subscription = new SubscriptionService(fixture.Database);
        // 北京时间 2026-09-01 02:00，处于 00:00–03:00 凌晨补抓窗口
        var timeProvider = new FakeTimeProvider(BeijingDateTime(2026, 9, 1, 2));
        var job = CreateJob(fetcher, subscription: subscription, timeProvider: timeProvider);

        await subscription.SubscribeAsync(Mid, SubscriptionType.Guard);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        // 首次添加立即抓取，快照记为前一日（8 月 31 日）
        await WaitForAsync(async () => (await guardRepo.GetMidsWithSnapshotAsync("2026-08-31")).Contains(Mid));
        Assert.Equal(1, calls);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-09-01"));

        // 9 月 1 日 23:00 的定时抓取不受影响，仍按当日差集正常抓取
        timeProvider.UtcNow = BeijingDateTime(2026, 9, 1, 23);
        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal([Mid], await guardRepo.GetMidsWithSnapshotAsync("2026-09-01"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OnSubscribed_BeforeCatchUpHour_ExistingYesterdaySnapshot_Skips()
    {
        await SeedAccountAsync(RoomId);
        var guardRepo = new LiveGuardRepository(fixture.Database);
        await guardRepo.UpsertDailySnapshotAsync(new LiveGuardRecord
        {
            Mid = Mid,
            RoomId = RoomId,
            Date = "2026-08-31",
            Guards = [],
            GuardCount = 0,
            FetchedAt = DateTime.UtcNow,
        });

        var fetchCalls = 0;
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) =>
            {
                fetchCalls++;
                return Task.FromResult<List<LiveGuardEntry>?>([]);
            }
        };
        var subscription = new SubscriptionService(fixture.Database);
        var job = CreateJob(fetcher, subscription: subscription, timeProvider: new FakeTimeProvider(BeijingDateTime(2026, 9, 1, 2)));

        await subscription.SubscribeAsync(Mid, SubscriptionType.Guard);
        await TestWait.NeverAsync(
            () => fetchCalls > 0,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(25),
            "existing yesterday snapshot must skip immediate crawl");

        Assert.Equal(0, fetchCalls);
    }

    [Fact]
    public async Task OnSubscribed_AfterCatchUpHourBeforeDailyHour_DoesNotCrawl()
    {
        await SeedAccountAsync(RoomId);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => throw new Exception("should not be called")
        };
        var subscription = new SubscriptionService(fixture.Database);
        // 北京时间 2026-09-01 04:00，过了凌晨补抓窗口、未到当日 23:00
        var job = CreateJob(fetcher, subscription: subscription, timeProvider: new FakeTimeProvider(BeijingDateTime(2026, 9, 1, 4)));

        await subscription.SubscribeAsync(Mid, SubscriptionType.Guard);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        await TestWait.NeverAsync(
            async () => (await guardRepo.GetMidsWithSnapshotAsync("2026-08-31")).Count > 0
                || (await guardRepo.GetMidsWithSnapshotAsync("2026-09-01")).Count > 0,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(25),
            "after catch-up window before daily hour must not crawl");
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-09-01"));
    }

    [Fact]
    public async Task Crawl_MissingRoomId_CrawlsAccountThenSnapshots()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        // 不预置 account，roomId 缺失
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => Task.FromResult<List<LiveGuardEntry>?>([
                new LiveGuardEntry { Uid = 1 },
            ])
        };
        var bili = new FakeBiliApiClient
        {
            AccountInfoProvider = _ => JsonDoc($"{{\"roomId\":{RoomId}}}"),
        };
        var accountRepo = new AccountBasicInfoRepository(fixture.Database);
        var accountInfoCrawler = new AccountInfoCrawler(
            bili,
            accountRepo,
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            NullLogger<AccountInfoCrawler>.Instance);

        var job = new LiveGuardJob(
            new SubscriptionService(fixture.Database),
            new FakeCookieServiceClient(),
            accountRepo,
            new LiveGuardRepository(fixture.Database),
            accountInfoCrawler,
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            GuardOptions(),
            new FakeTimeProvider(BeijingDateTime(2026, 8, 31, 23)),
            NullLogger<LiveGuardJob>.Instance,
            fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Equal([Mid], await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_MissingRoomId_AccountCrawlFails_SkipsWithoutSnapshot()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => Task.FromResult<List<LiveGuardEntry>?>([])
        };
        var bili = new FakeBiliApiClient
        {
            AccountInfoProvider = _ => throw new Exception("crawl failed"),
        };
        var accountRepo = new AccountBasicInfoRepository(fixture.Database);
        var accountInfoCrawler = new AccountInfoCrawler(
            bili,
            accountRepo,
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            NullLogger<AccountInfoCrawler>.Instance);

        var job = new LiveGuardJob(
            new SubscriptionService(fixture.Database),
            new FakeCookieServiceClient(),
            accountRepo,
            new LiveGuardRepository(fixture.Database),
            accountInfoCrawler,
            new CrawlLockProvider(new FakeDistributedLockProvider()),
            GuardOptions(),
            new FakeTimeProvider(BeijingDateTime(2026, 8, 31, 23)),
            NullLogger<LiveGuardJob>.Instance,
            fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_FetcherReturnsNull_DoesNotUpsert()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => Task.FromResult<List<LiveGuardEntry>?>(null)
        };
        var job = CreateJob(fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_NoAvailableCookie_SkipsWithoutUpsert()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => throw new Exception("should not be called")
        };
        var noCookieClient = new NoCookieServiceClient();
        var job = CreateJob(fetcher, cookieServiceClient: noCookieClient);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_EmptyFetchResult_PersistsEmptySnapshot()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var fetcher = new FakeLiveGuardListFetcher
        {
            FetchFunc = (_, _, _, _, _) => Task.FromResult<List<LiveGuardEntry>?>([])
        };
        var job = CreateJob(fetcher);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(LiveCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Empty(stored.Guards);
        Assert.Equal(0, stored.GuardCount);
    }

    private sealed class NoCookieServiceClient : ICookieServiceClient
    {
        public Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default) => throw new NoAvailableCookieException();
        public Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default) => Task.FromResult(new ReportInvalidResult { Status = ReportInvalidStatus.Refreshed, CookieId = cookieId });
        public Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WeiboCookieCheckoutResult> CheckoutWeiboAsync(CancellationToken ct = default) => Task.FromResult(new WeiboCookieCheckoutResult { Available = false });
        public Task<ReportInvalidResult> ReportWeiboInvalidAsync(string cookieId, CancellationToken ct = default) => Task.FromResult(new ReportInvalidResult { Status = ReportInvalidStatus.Refreshed, CookieId = cookieId });
        public Task CooldownWeiboAsync(string cookieId, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static System.Text.Json.JsonElement JsonDoc(string channelBody)
    {
        var json = $"{{\"code\":200,\"data\":{{\"channel\":{channelBody}}}}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}