using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveGuardRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static LiveGuardRecord Record(long mid, string date, long roomId = 123456, params (long Uid, string Name, int GuardLevel, string? MedalName, int MedalLevel)[] guards) => new()
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

    [Fact]
    public async Task Upsert_NewDay_InsertsSingleRow()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0)));

        var mids = await repository.GetMidsWithSnapshotAsync("2026-08-31");
        Assert.Equal([1001L], mids);
    }

    [Fact]
    public async Task Upsert_SameDay_OverwritesIdempotently()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0), (2, "b", 2, null, 0)));

        var mids = await repository.GetMidsWithSnapshotAsync("2026-08-31");
        Assert.Equal([1001L], mids);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001).FirstOrDefaultAsync();
        Assert.Equal(2, stored.Guards.Count);
        Assert.Equal(2, stored.GuardCount);
    }

    [Fact]
    public async Task GetMidsWithSnapshot_OnlyReturnsMidsWithSnapshotForDate()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1002, "2026-08-31", 123456, (2, "b", 2, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1003, "2026-08-30", 123456, (3, "c", 3, null, 0)));

        var mids = await repository.GetMidsWithSnapshotAsync("2026-08-31");
        Assert.Equal([1001L, 1002L], mids.OrderBy(m => m).ToList());
    }

    [Fact]
    public async Task GetMidsWithSnapshot_NoRows_ReturnsEmpty()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        Assert.Empty(await repository.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task DifferentDaysForSameMid_DoNotInterfere()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (2, "b", 2, null, 0)));

        Assert.Equal([1001L], await repository.GetMidsWithSnapshotAsync("2026-08-30"));
        Assert.Equal([1001L], await repository.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Upsert_WithTop3Entries_IncludesAllFields()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456,
            (1, "regular", 3, "星", 5),
            (2, "former_guard", 5, "舰", 10)));

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Guards.Count);
        Assert.Equal(2, stored.GuardCount);

        var entry1 = stored.Guards.First(g => g.Uid == 1);
        Assert.Equal("regular", entry1.UserName);
        Assert.Equal(3, entry1.GuardLevel);
        Assert.Equal("星", entry1.MedalName);
        Assert.Equal(5, entry1.MedalLevel);

        var entry2 = stored.Guards.First(g => g.Uid == 2);
        Assert.Equal("former_guard", entry2.UserName);
        Assert.Equal(5, entry2.GuardLevel);
        Assert.Equal("舰", entry2.MedalName);
        Assert.Equal(10, entry2.MedalLevel);
    }

    [Fact]
    public async Task Upsert_SameDay_WithTop3Entries_OverwritesAndRemainsSingleRow()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456,
            (1, "user1", 3, null, 0),
            (2, "top3_user", 5, null, 0)));

        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456,
            (1, "user1", 3, null, 0),
            (2, "top3_user", 5, null, 0),
            (3, "user3", 4, null, 0)));

        var mids = await repository.GetMidsWithSnapshotAsync("2026-08-31");
        Assert.Equal([1001L], mids);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001).FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(3, stored.Guards.Count);
        Assert.Equal(3, stored.GuardCount);
        var uids = stored.Guards.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L, 3L], uids);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_AnyMode_DeduplicatesAcrossDays()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        // 同一 uid 在两天都有快照，any 口径应只出现一次
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, "medal1", 10)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, "medal1", 10)));

        var dates = new[] { "2026-08-30", "2026-08-31" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: false);

        Assert.Single(result);
        Assert.Equal(1, result[0].Uid);
        Assert.Equal("a", result[0].UserName);
        Assert.Equal(1, result[0].GuardLevel);
        Assert.Equal("medal1", result[0].MedalName);
        Assert.Equal(10, result[0].MedalLevel);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_AnyMode_TakesLatestSnapshot()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        // 同一 uid 多天名单信息不一致时取范围内最新一天快照
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "old_name", 1, "old_medal", 5)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "new_name", 2, "new_medal", 10)));

        var dates = new[] { "2026-08-30", "2026-08-31" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: false);

        Assert.Single(result);
        Assert.Equal("new_name", result[0].UserName);
        Assert.Equal(2, result[0].GuardLevel);
        Assert.Equal("new_medal", result[0].MedalName);
        Assert.Equal(10, result[0].MedalLevel);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_AllMode_OnlyReturnsUidsWithAllDays()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        // uid 1 在两天都有快照，uid 2 只有一天
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0), (2, "b", 2, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0)));

        var dates = new[] { "2026-08-30", "2026-08-31" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: true);

        Assert.Single(result);
        Assert.Equal(1, result[0].Uid);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_AllMode_MissingDayReturnsEmpty()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        // all 口径下某天无快照则结果为空
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        // 2026-08-31 无快照

        var dates = new[] { "2026-08-30", "2026-08-31" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: true);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_OtherRoomId_NotMixed()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        // 其他 RoomId 的快照不混入（不同 Mid + 同一日期 + 不同 RoomId）
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1002, "2026-08-30", 999999, (2, "b", 2, null, 0)));

        var dates = new[] { "2026-08-30" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: false);

        Assert.Single(result);
        Assert.Equal(1, result[0].Uid);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_OutOfRange_NotIncluded()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        // 范围之外日期的快照不影响结果
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-29", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-09-01", 123456, (1, "a", 1, null, 0)));

        var dates = new[] { "2026-08-30" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: false);

        Assert.Single(result);
        Assert.Equal(1, result[0].Uid);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_NoData_ReturnsEmpty()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        var dates = new[] { "2026-08-30", "2026-08-31" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: false);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_EmptyDates_ReturnsEmpty()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));

        var result = await repository.GetOnboardGuardEntriesAsync(123456, [], requireAllDays: false);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetOnboardGuardEntries_ResultsOrderedByUid()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (3, "c", 3, null, 0), (1, "a", 1, null, 0), (2, "b", 2, null, 0)));

        var dates = new[] { "2026-08-30" };
        var result = await repository.GetOnboardGuardEntriesAsync(123456, dates, requireAllDays: false);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].Uid);
        Assert.Equal(2, result[1].Uid);
        Assert.Equal(3, result[2].Uid);
    }

    [Fact]
    public async Task GetDistinctMidsAsync_MultiMidsMultiDays_ReturnsDistinct()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (2, "b", 2, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1002, "2026-08-30", 123456, (3, "c", 3, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1003, "2026-08-31", 123456, (4, "d", 4, null, 0)));

        var result = await repository.GetDistinctMidsAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal([1001L, 1002L, 1003L], result.OrderBy(m => m).ToList());
    }

    [Fact]
    public async Task GetDistinctMidsAsync_NoData_ReturnsEmpty()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        var result = await repository.GetDistinctMidsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSnapshotsByMidAsync_ReturnsOnlySpecifiedMid()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-29", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (2, "b", 2, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1002, "2026-08-29", 123456, (3, "c", 3, null, 0)));

        var result = await repository.GetSnapshotsByMidAsync(1001, "2026-08-31");

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(1001, r.Mid));
    }

    [Fact]
    public async Task GetSnapshotsByMidAsync_ExcludesBeforeDateExclusive()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-29", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (2, "b", 2, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (3, "c", 3, null, 0)));

        var result = await repository.GetSnapshotsByMidAsync(1001, "2026-08-31");

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.True(string.Compare(r.Date, "2026-08-31", StringComparison.Ordinal) < 0));
    }

    [Fact]
    public async Task GetSnapshotsByMidAsync_OrderByDateAscending()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (3, "c", 3, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-29", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (2, "b", 2, null, 0)));

        var result = await repository.GetSnapshotsByMidAsync(1001, "2026-09-01");

        Assert.Equal(3, result.Count);
        Assert.Equal("2026-08-29", result[0].Date);
        Assert.Equal("2026-08-30", result[1].Date);
        Assert.Equal("2026-08-31", result[2].Date);
    }

    [Fact]
    public async Task GetSnapshotsByMidAsync_NoData_ReturnsEmpty()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        var result = await repository.GetSnapshotsByMidAsync(1001, "2026-08-31");
        Assert.Empty(result);
    }

    [Fact]
    public async Task BackfillMissingEntriesAsync_AppendsEntries()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));

        var missing = new List<LiveGuardEntry>
        {
            new() { Uid = 2, UserName = "b", GuardLevel = 2, MedalName = null, MedalLevel = 0 },
            new() { Uid = 3, UserName = "c", GuardLevel = 3, MedalName = null, MedalLevel = 0 },
        };
        await repository.BackfillMissingEntriesAsync(1001, "2026-08-30", missing, 3);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001 && r.Date == "2026-08-30").FirstOrDefaultAsync();
        Assert.NotNull(stored);
        Assert.Equal(3, stored.GuardCount);
        Assert.Equal(3, stored.Guards.Count);
        var uids = stored.Guards.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L, 3L], uids);
    }

    [Fact]
    public async Task BackfillMissingEntriesAsync_ExistingEntriesUnchanged()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 3, "星", 5)));

        var missing = new List<LiveGuardEntry>
        {
            new() { Uid = 2, UserName = "b", GuardLevel = 2, MedalName = null, MedalLevel = 0 },
        };
        await repository.BackfillMissingEntriesAsync(1001, "2026-08-30", missing, 2);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001 && r.Date == "2026-08-30").FirstOrDefaultAsync();
        Assert.NotNull(stored);
        var entry1 = stored.Guards.First(g => g.Uid == 1);
        Assert.Equal("a", entry1.UserName);
        Assert.Equal(3, entry1.GuardLevel);
        Assert.Equal("星", entry1.MedalName);
        Assert.Equal(5, entry1.MedalLevel);
    }

    [Fact]
    public async Task BackfillMissingEntriesAsync_GuardCountSync()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));

        var missing = new List<LiveGuardEntry>
        {
            new() { Uid = 2, UserName = "b", GuardLevel = 2, MedalName = null, MedalLevel = 0 },
        };
        await repository.BackfillMissingEntriesAsync(1001, "2026-08-30", missing, 2);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001 && r.Date == "2026-08-30").FirstOrDefaultAsync();
        Assert.Equal(stored.Guards.Count, stored.GuardCount);
    }

    [Fact]
    public async Task BackfillMissingEntriesAsync_OtherMidNotAffected()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1002, "2026-08-30", 123456, (2, "b", 2, null, 0)));

        var missing = new List<LiveGuardEntry>
        {
            new() { Uid = 3, UserName = "c", GuardLevel = 3, MedalName = null, MedalLevel = 0 },
        };
        await repository.BackfillMissingEntriesAsync(1001, "2026-08-30", missing, 2);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored2 = await guards.Find(r => r.Mid == 1002 && r.Date == "2026-08-30").FirstOrDefaultAsync();
        Assert.Single(stored2.Guards);
        Assert.Equal(2L, stored2.Guards[0].Uid);
    }

    [Fact]
    public async Task BackfillMissingEntriesAsync_OtherDateNotAffected()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-29", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (2, "b", 2, null, 0)));

        var missing = new List<LiveGuardEntry>
        {
            new() { Uid = 3, UserName = "c", GuardLevel = 3, MedalName = null, MedalLevel = 0 },
        };
        await repository.BackfillMissingEntriesAsync(1001, "2026-08-30", missing, 2);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored29 = await guards.Find(r => r.Mid == 1001 && r.Date == "2026-08-29").FirstOrDefaultAsync();
        Assert.Single(stored29.Guards);
        Assert.Equal(1L, stored29.Guards[0].Uid);
    }

    [Fact]
    public async Task BackfillMissingEntriesAsync_EmptyMissing_DoesNotWrite()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));

        var missing = new List<LiveGuardEntry>();
        await repository.BackfillMissingEntriesAsync(1001, "2026-08-30", missing, 1);

        var guards = fixture.Database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var stored = await guards.Find(r => r.Mid == 1001 && r.Date == "2026-08-30").FirstOrDefaultAsync();
        Assert.Single(stored.Guards);
        Assert.Equal(1, stored.GuardCount);
    }
}
