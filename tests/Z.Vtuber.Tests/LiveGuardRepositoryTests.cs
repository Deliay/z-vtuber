using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveGuardRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static LiveGuardRecord Record(long mid, string date, params (long Uid, string Name)[] guards) => new()
    {
        Id = MongoDB.Bson.ObjectId.GenerateNewId(),
        Mid = mid,
        RoomId = 123456,
        Date = date,
        Guards = guards.Select(g => new LiveGuardEntry { Uid = g.Uid, UserName = g.Name }).ToList(),
        GuardCount = guards.Length,
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Upsert_NewDay_InsertsSingleRow()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", (1, "a")));

        var mids = await repository.GetMidsWithSnapshotAsync("2026-08-31");
        Assert.Equal([1001L], mids);
    }

    [Fact]
    public async Task Upsert_SameDay_OverwritesIdempotently()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", (1, "a")));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", (1, "a"), (2, "b")));

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
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", (1, "a")));
        await repository.UpsertDailySnapshotAsync(Record(1002, "2026-08-31", (2, "b")));
        await repository.UpsertDailySnapshotAsync(Record(1003, "2026-08-30", (3, "c")));

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
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", (1, "a")));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", (2, "b")));

        Assert.Equal([1001L], await repository.GetMidsWithSnapshotAsync("2026-08-30"));
        Assert.Equal([1001L], await repository.GetMidsWithSnapshotAsync("2026-08-31"));
    }

    [Fact]
    public async Task Upsert_WithTop3Entries_IncludesAllFields()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        var record = new LiveGuardRecord
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            Mid = 1001,
            RoomId = 123456,
            Date = "2026-08-31",
            Guards =
            [
                new LiveGuardEntry { Uid = 1, UserName = "regular", GuardLevel = 3, MedalName = "星", MedalLevel = 5 },
                new LiveGuardEntry { Uid = 2, UserName = "former_guard", GuardLevel = 5, MedalName = "舰", MedalLevel = 10 },
            ],
            GuardCount = 2,
            FetchedAt = DateTime.UtcNow,
        };
        await repository.UpsertDailySnapshotAsync(record);

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
        var first = new LiveGuardRecord
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            Mid = 1001,
            RoomId = 123456,
            Date = "2026-08-31",
            Guards =
            [
                new LiveGuardEntry { Uid = 1, UserName = "user1", GuardLevel = 3 },
                new LiveGuardEntry { Uid = 2, UserName = "top3_user", GuardLevel = 5 },
            ],
            GuardCount = 2,
            FetchedAt = DateTime.UtcNow,
        };
        await repository.UpsertDailySnapshotAsync(first);

        var second = new LiveGuardRecord
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            Mid = 1001,
            RoomId = 123456,
            Date = "2026-08-31",
            Guards =
            [
                new LiveGuardEntry { Uid = 1, UserName = "user1", GuardLevel = 3 },
                new LiveGuardEntry { Uid = 2, UserName = "top3_user", GuardLevel = 5 },
                new LiveGuardEntry { Uid = 3, UserName = "user3", GuardLevel = 4 },
            ],
            GuardCount = 3,
            FetchedAt = DateTime.UtcNow,
        };
        await repository.UpsertDailySnapshotAsync(second);

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
}