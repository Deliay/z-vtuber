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

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveGuardJobTests(MongoDbFixture fixture) : IAsyncLifetime
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

    private sealed class CountingGuardJob : LiveGuardJob
    {
        public int DelayCount { get; private set; }

        public CountingGuardJob(
            ISubscriptionService subscription,
            IBiliApiClient bili,
            ICookiePoolService cookiePool,
            AccountBasicInfoRepository accountRepository,
            LiveGuardRepository guardRepository,
            IAccountInfoCrawler accountInfoCrawler,
            CrawlLockProvider locks,
            BilibiliCrawlerOptions options,
            TimeProvider timeProvider,
            ILogger<LiveGuardJob> logger)
            : base(subscription, bili, cookiePool, accountRepository, guardRepository,
                accountInfoCrawler, locks, options, timeProvider, logger)
        {
        }

        internal override Task DelayBetweenPagesAsync(CancellationToken ct)
        {
            DelayCount++;
            return Task.CompletedTask;
        }
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
        var subscriptions = fixture.Database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(new SubscriptionRecord
        {
            Mid = Mid,
            Type = type,
            SubscribedAt = DateTime.UtcNow,
        });
    }

    private async Task SeedAccountAsync(long roomId)
    {
        var accounts = fixture.Database.GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accounts.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = Mid,
            RoomId = roomId,
            Data = "{}",
            FetchedAt = DateTime.UtcNow,
        });
    }

    private CountingGuardJob CreateJob(
        FakeBiliApiClient bili,
        DateTime? utcNow = null,
        AccountBasicInfoRepository? accountRepo = null,
        BilibiliCrawlerOptions? options = null,
        ICookiePoolService? cookiePool = null,
        ISubscriptionService? subscription = null,
        FakeTimeProvider? timeProvider = null)
    {
        accountRepo ??= new AccountBasicInfoRepository(fixture.Database);
        var accountInfoCrawler = new AccountInfoCrawler(
            bili,
            accountRepo,
            new LiveSessionRepository(fixture.Database),
            new CrawlLockProvider(),
            NullLogger<AccountInfoCrawler>.Instance);

        return new CountingGuardJob(
            subscription ?? new SubscriptionService(fixture.Database),
            bili,
            cookiePool ?? new FakeCookiePoolService(),
            accountRepo,
            new LiveGuardRepository(fixture.Database),
            accountInfoCrawler,
            new CrawlLockProvider(),
            options ?? GuardOptions(),
            timeProvider ?? new FakeTimeProvider(utcNow ?? BeijingDateTime(2026, 8, 31, 23)),
            NullLogger<LiveGuardJob>.Instance);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("condition not met within timeout");
    }

    [Fact]
    public async Task Run_BeforeDailyHour_DoesNotCrawl()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new Exception("should not be called") };
        var job = CreateJob(bili, utcNow: BeijingDateTime(2026, 8, 31, 22));

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Run_AfterDailyHour_SnapshotsPendingMid()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) => new GuardInfo
            {
                Info = new GuardInfo.PageInfo { Count = 1, Current = 1, PageCount = 1 },
                List = [new GuardUserInfo { Uid = 1, UserName = "a", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } }],
            }
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
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

        var calls = 0;
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => { calls++; return new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] }; } };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_EmptyDiff_MakesNoRequests()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var guardRepo = new LiveGuardRepository(fixture.Database);
        await guardRepo.UpsertDailySnapshotAsync(new LiveGuardRecord { Mid = Mid, RoomId = RoomId, Date = "2026-08-31", Guards = [], GuardCount = 0, FetchedAt = DateTime.UtcNow });

        var calls = 0;
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => { calls++; return new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] }; } };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OnSubscribed_NonGuard_DoesNotTrigger()
    {
        await SeedSubscriptionAsync(SubscriptionType.Danmaku);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new Exception("should not be called") };
        var subscription = new SubscriptionService(fixture.Database);
        var job = CreateJob(bili, subscription: subscription);

        await subscription.SubscribeAsync(Mid, SubscriptionType.Danmaku);

        await Task.Delay(300);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task OnSubscribed_BeforeCatchUpHour_CrawlsAsYesterdaySnapshot_AndDailyRunStillWorks()
    {
        await SeedAccountAsync(RoomId);
        var calls = 0;
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) =>
            {
                calls++;
                return new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [new GuardUserInfo { Uid = 1 }] };
            }
        };
        var subscription = new SubscriptionService(fixture.Database);
        // 北京时间 2026-09-01 02:00，处于 00:00–03:00 凌晨补抓窗口
        var timeProvider = new FakeTimeProvider(BeijingDateTime(2026, 9, 1, 2));
        var job = CreateJob(bili, subscription: subscription, timeProvider: timeProvider);

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

        var calls = 0;
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => { calls++; return new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] }; } };
        var subscription = new SubscriptionService(fixture.Database);
        var job = CreateJob(bili, subscription: subscription, timeProvider: new FakeTimeProvider(BeijingDateTime(2026, 9, 1, 2)));

        await subscription.SubscribeAsync(Mid, SubscriptionType.Guard);
        await Task.Delay(300);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OnSubscribed_AfterCatchUpHourBeforeDailyHour_DoesNotCrawl()
    {
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new Exception("should not be called") };
        var subscription = new SubscriptionService(fixture.Database);
        // 北京时间 2026-09-01 04:00，过了凌晨补抓窗口、未到当日 23:00
        var job = CreateJob(bili, subscription: subscription, timeProvider: new FakeTimeProvider(BeijingDateTime(2026, 9, 1, 4)));

        await subscription.SubscribeAsync(Mid, SubscriptionType.Guard);
        await Task.Delay(300);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-09-01"));
    }

    [Fact]
    public async Task Crawl_MissingRoomId_CrawlsAccountThenSnapshots()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        // 不预置 account，roomId 缺失
        var bili = new FakeBiliApiClient
        {
            AccountInfoProvider = _ => JsonDoc($"{{\"roomId\":{RoomId}}}"),
            RoomGuardListProvider = (_, _) => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [new GuardUserInfo { Uid = 1 }] },
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Equal([Mid], await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_MissingRoomId_AccountCrawlFails_SkipsWithoutSnapshot()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        var bili = new FakeBiliApiClient
        {
            AccountInfoProvider = _ => throw new Exception("crawl failed"),
            RoomGuardListProvider = (_, _) => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_MultiPage_MergesAllPages()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var pageData = new Dictionary<int, GuardInfo>
        {
            [1] = new() { Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 2 }, List = [new GuardUserInfo { Uid = 1 }, new GuardUserInfo { Uid = 2 }] },
            [2] = new() { Info = new GuardInfo.PageInfo { Count = 2, Current = 2, PageCount = 2 }, List = [new GuardUserInfo { Uid = 3 }] },
        };
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, page) => pageData[page] };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(3, stored.GuardCount);
        Assert.Equal([1L, 2L, 3L], stored.Guards.Select(g => g.Uid).OrderBy(u => u).ToList());
        // 2 页 → 1 次页间延迟
        Assert.Equal(1, job.DelayCount);
    }

    [Fact]
    public async Task Crawl_EmptyFirstPage_PersistsEmptySnapshot()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => new GuardInfo { Info = new GuardInfo.PageInfo { Count = 0, Current = 1, PageCount = 1 }, List = [] } };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Empty(stored.Guards);
        Assert.Equal(0, stored.GuardCount);
    }

    [Fact]
    public async Task Crawl_SinglePageFailure_DoesNotUpsert()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new Exception("boom") };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_BackoffActive_SkipsRoundWithoutUpsert()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new CrawlBackoffActiveException("backoff", TimeSpan.FromMinutes(1)) };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_NoAvailableCookie_SkipsWithoutUpsert()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new Exception("should not be called") };
        var cookiePool = new NoCookiePoolService();
        var job = CreateJob(bili, cookiePool: cookiePool);

        await job.RunOnceAsync(CancellationToken.None);

        var guardRepo = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await guardRepo.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Crawl_PageCountCap_LimitsRequests()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var options = GuardOptions();
        options.GuardMaxPages = 2;
        var pagesRequested = 0;
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) =>
            {
                pagesRequested++;
                return new GuardInfo { Info = new GuardInfo.PageInfo { Count = 100, Current = pagesRequested, PageCount = 100 }, List = [new GuardUserInfo { Uid = pagesRequested }] };
            }
        };
        var job = CreateJob(bili, options: options);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, pagesRequested);
        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.Equal(2, stored.GuardCount);
        Assert.Equal(1, job.DelayCount);
    }

    [Fact]
    public async Task Crawl_FirstPageTop3_MergesIntoGuardList()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [new GuardUserInfo { Uid = 3, UserName = "top3_user", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "medal", Level = 10 } }],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(3, stored.GuardCount);
        Assert.Equal(3, stored.Guards.Count);
        var uids = stored.Guards.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L, 3L], uids);
        var top3Entry = stored.Guards.First(g => g.Uid == 3);
        Assert.Equal("top3_user", top3Entry.UserName);
        Assert.Equal(5, top3Entry.GuardLevel);
        Assert.Equal("medal", top3Entry.MedalName);
        Assert.Equal(10, top3Entry.MedalLevel);
    }

    [Fact]
    public async Task Crawl_Top3OverlapWithList_DeduplicatesByUid()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [new GuardUserInfo { Uid = 1, UserName = "top3_user1_dup" }],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(2, stored.GuardCount);
        Assert.Equal(2, stored.Guards.Count);
        var uids = stored.Guards.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L], uids);
    }

    [Fact]
    public async Task Crawl_Top3Null_FallsBackToGuardListOnly()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = null!,
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(2, stored.GuardCount);
        Assert.Equal(2, stored.Guards.Count);
    }

    [Fact]
    public async Task Crawl_Top3EmptyList_FallsBackToGuardListOnly()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(2, stored.GuardCount);
        Assert.Equal(2, stored.Guards.Count);
    }

    [Fact]
    public async Task Crawl_MultiPageTop3_NotMergedBeyondFirstPage()
    {
        await SeedSubscriptionAsync(SubscriptionType.Guard);
        await SeedAccountAsync(RoomId);
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 1, Current = 1, PageCount = 2 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }],
                    Top3 = [new GuardUserInfo { Uid = 10, UserName = "top3_page1" }],
                },
                2 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 1, Current = 2, PageCount = 2 },
                    List = [new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [new GuardUserInfo { Uid = 20, UserName = "top3_page2_should_not_merge" }],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var job = CreateJob(bili);

        await job.RunOnceAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == Mid).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(3, stored.GuardCount);
        var uids = stored.Guards.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L, 10L], uids);
    }

    private sealed class NoCookiePoolService : ICookiePoolService
    {
        public Task<CookieRecord> AddAsync(string cookie, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CookieRecord> NextAsync(CancellationToken ct = default) => throw new NoAvailableCookieException();
        public Task MarkOfflineAsync(string cookie, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CookieRecord>>([]);
    }

    private static System.Text.Json.JsonElement JsonDoc(string channelBody)
    {
        var json = $"{{\"code\":200,\"data\":{{\"channel\":{channelBody}}}}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}