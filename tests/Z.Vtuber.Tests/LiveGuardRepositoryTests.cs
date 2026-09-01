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
}