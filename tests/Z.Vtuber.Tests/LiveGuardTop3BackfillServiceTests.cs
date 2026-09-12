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

[Collection("mongo")]
public class LiveGuardTop3BackfillServiceTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const long Mid1 = 1001;
    private const long Mid2 = 1002;
    private const long RoomId1 = 123456;
    private const long RoomId2 = 789012;

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
        public override long TimestampFrequency => 1_000_000;
    }

    private static DateTime BeijingDateTime(int year, int month, int day, int hour) =>
        new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-8);

    private static LiveGuardRecord Record(long mid, string date, long roomId = RoomId1, params (long Uid, string Name, int GuardLevel, string? MedalName, int MedalLevel)[] guards) => new()
    {
        Id = MongoDB.Bson.ObjectId.GenerateNewId(),
        Mid = mid,
        RoomId = roomId,
        Date = date,
        Guards = guards.Select(g => new LiveGuardEntry
        {
            Uid = g.Uid,
            UserName = g.Name,
            GuardLevel = g.GuardLevel,
            MedalName = g.MedalName,
            MedalLevel = g.MedalLevel,
        }).ToList(),
        GuardCount = guards.Length,
        FetchedAt = DateTime.UtcNow,
    };

    private async Task SeedAccountAsync(long mid, long roomId)
    {
        var accounts = fixture.Database.GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accounts.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = roomId,
            Data = "{}",
            FetchedAt = DateTime.UtcNow,
        });
    }

    private LiveGuardTop3BackfillService CreateService(
        FakeBiliApiClient bili,
        DateTime? utcNow = null,
        ICookieServiceClient? cookieServiceClient = null,
        FakeTimeProvider? timeProvider = null)
    {
        var accountRepo = new AccountBasicInfoRepository(fixture.Database);
        var guardRepo = new LiveGuardRepository(fixture.Database);

        return new LiveGuardTop3BackfillService(
            accountRepo,
            guardRepo,
            cookieServiceClient ?? new FakeCookieServiceClient(),
            new AccountInfoCrawler(
                bili,
                accountRepo,
                new LiveSessionRepository(fixture.Database),
                new CrawlLockProvider(new FakeDistributedLockProvider()),
                NullLogger<AccountInfoCrawler>.Instance),
            fixture.Database,
            new LiveGuardListFetcher(bili, new BilibiliCrawlerOptions(), NullLogger<LiveGuardListFetcher>.Instance),
            timeProvider ?? new FakeTimeProvider(utcNow ?? BeijingDateTime(2026, 9, 1, 23)),
            NullLogger<LiveGuardTop3BackfillService>.Instance);
    }

    private async Task<bool> IsMarkedAsync(long mid)
    {
        var metaCollection = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var key = $"live_guard_top3_backfill_v1:{mid}";
        var filter = Builders<MetaRecord>.Filter.Eq(m => m.Key, key);
        var result = await metaCollection.Find(filter).FirstOrDefaultAsync();
        return result is not null;
    }

    [Fact]
    public async Task ExecuteAsync_BackfillsHistoricalSnapshots()
    {
        await SeedAccountAsync(Mid1, RoomId1);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-29", RoomId1, (1, "existing1", 3, "星", 5)));
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (2, "existing2", 5, "舰", 10)));

        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) => new GuardInfo
            {
                Info = new GuardInfo.PageInfo { PageCount = 1 },
                List = [
                    new GuardUserInfo { Uid = 1, UserName = "user1", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } },
                    new GuardUserInfo { Uid = 2, UserName = "user2", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 10 } },
                    new GuardUserInfo { Uid = 3, UserName = "top3_user", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 15 } },
                ],
            }
        };
        var service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        var snapshot29 = await repo.GetSnapshotsByMidAsync(Mid1, "2026-09-01");
        Assert.Equal(2, snapshot29.Count);
        Assert.Equal("2026-08-29", snapshot29[0].Date);
        Assert.Equal("2026-08-30", snapshot29[1].Date);

        var guards29 = snapshot29[0].Guards;
        Assert.Equal(3, guards29.Count);
        Assert.Equal(3, snapshot29[0].GuardCount);
        Assert.True(await IsMarkedAsync(Mid1));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotBackfillTodaySnapshot()
    {
        await SeedAccountAsync(Mid1, RoomId1);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (1, "existing1", 3, "星", 5)));

        var today = "2026-09-01";
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) => new GuardInfo
            {
                Info = new GuardInfo.PageInfo { PageCount = 1 },
                List = [
                    new GuardUserInfo { Uid = 1, UserName = "user1", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } },
                    new GuardUserInfo { Uid = 2, UserName = "user2", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 10 } },
                ],
            }
        };
        var service = CreateService(bili, utcNow: BeijingDateTime(2026, 9, 1, 23));

        await service.ExecuteAsync(CancellationToken.None);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var todaySnapshot = await guards.Find(r => r.Mid == Mid1 && r.Date == today).FirstOrDefaultAsync();
        Assert.Null(todaySnapshot);
    }

    [Fact]
    public async Task ExecuteAsync_FetchFailure_SkipsMidWithoutMarking()
    {
        await SeedAccountAsync(Mid1, RoomId1);
        await SeedAccountAsync(Mid2, RoomId2);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (1, "existing1", 3, "星", 5)));
        await repo.UpsertDailySnapshotAsync(Record(Mid2, "2026-08-30", RoomId2, (2, "existing2", 5, "舰", 10)));

        var callCount = 0;
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (roomId, _) =>
            {
                callCount++;
                if (roomId == RoomId1)
                    throw new Exception("fetch failed");
                return new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { PageCount = 1 },
                    List = [
                        new GuardUserInfo { Uid = 2, UserName = "user2", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 10 } },
                        new GuardUserInfo { Uid = 3, UserName = "top3_user", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 15 } },
                    ],
                };
            }
        };
        var service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        Assert.False(await IsMarkedAsync(Mid1));
        Assert.True(await IsMarkedAsync(Mid2));
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulWriteMark()
    {
        await SeedAccountAsync(Mid1, RoomId1);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (1, "existing1", 3, "星", 5)));

        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) => new GuardInfo
            {
                Info = new GuardInfo.PageInfo { PageCount = 1 },
                List = [
                    new GuardUserInfo { Uid = 1, UserName = "user1", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } },
                    new GuardUserInfo { Uid = 2, UserName = "user2", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 10 } },
                ],
            }
        };
        var service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        Assert.True(await IsMarkedAsync(Mid1));
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyMarked_SkipsFetching()
    {
        await SeedAccountAsync(Mid1, RoomId1);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (1, "existing1", 3, "星", 5)));

        var metaCollection = fixture.Database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        await metaCollection.InsertOneAsync(new MetaRecord { Key = $"live_guard_top3_backfill_v1:{Mid1}" });

        var fetchCalls = 0;
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) =>
            {
                fetchCalls++;
                return new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { PageCount = 1 },
                    List = [
                        new GuardUserInfo { Uid = 1, UserName = "user1", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } },
                    ],
                };
            }
        };
        var service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, fetchCalls);
    }

    [Fact]
    public async Task ExecuteAsync_UnmarkedMidSecondRun_Idempotent()
    {
        await SeedAccountAsync(Mid1, RoomId1);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (1, "existing1", 3, "星", 5)));

        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) => new GuardInfo
            {
                Info = new GuardInfo.PageInfo { PageCount = 1 },
                List = [
                    new GuardUserInfo { Uid = 1, UserName = "user1", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } },
                    new GuardUserInfo { Uid = 2, UserName = "user2", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 10 } },
                ],
            }
        };
        var service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        bili.RoomGuardListProvider = (_, _) => new GuardInfo
        {
            Info = new GuardInfo.PageInfo { PageCount = 1 },
            List = [
                new GuardUserInfo { Uid = 1, UserName = "user1", GuardType = 3, MedalInfo = new GuardUserInfo.Medal { Name = "星", Level = 5 } },
                new GuardUserInfo { Uid = 2, UserName = "user2", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "舰", Level = 10 } },
            ],
        };
        service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        var snapshot29 = await repo.GetSnapshotsByMidAsync(Mid1, "2026-09-01");
        Assert.Single(snapshot29);
        Assert.Equal(2, snapshot29[0].GuardCount);
    }

    [Fact]
    public async Task ExecuteAsync_MissingRoomId_SkipsWithoutMarking()
    {
        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", 0, (1, "existing1", 3, "星", 5)));

        var bili = new FakeBiliApiClient
        {
            AccountInfoProvider = _ => throw new Exception("crawl failed"),
            RoomGuardListProvider = (_, _) => new GuardInfo
            {
                Info = new GuardInfo.PageInfo { PageCount = 1 },
                List = [],
            },
        };
        var service = CreateService(bili);

        await service.ExecuteAsync(CancellationToken.None);

        Assert.False(await IsMarkedAsync(Mid1));
    }

    [Fact]
    public async Task ExecuteAsync_NoAvailableCookie_ExitsRound()
    {
        await SeedAccountAsync(Mid1, RoomId1);

        var repo = new LiveGuardRepository(fixture.Database);
        await repo.UpsertDailySnapshotAsync(Record(Mid1, "2026-08-30", RoomId1, (1, "existing1", 3, "星", 5)));

        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) => throw new Exception("should not be called"),
        };
        var cookieServiceClient = new NoCookieServiceClient();
        var service = CreateService(bili, cookieServiceClient: cookieServiceClient);

        await service.ExecuteAsync(CancellationToken.None);

        Assert.False(await IsMarkedAsync(Mid1));
    }

    private sealed class NoCookieServiceClient : ICookieServiceClient
    {
        public Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default) => throw new NoAvailableCookieException();
        public Task ReportInvalidAsync(string cookieId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;
    }
}