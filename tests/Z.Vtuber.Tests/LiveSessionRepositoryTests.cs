using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class LiveSessionRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static LiveSessionRecord Record(string liveId, DateTime? startDate = null) => new()
    {
        LiveId = liveId,
        RoomId = 123456,
        Title = "title",
        StartDate = startDate ?? DateTime.UtcNow,
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task UpsertMany_DuplicatesInBatch_AreDeduplicated()
    {
        var repository = new LiveSessionRepository(fixture.Database);

        await repository.UpsertManyAsync([Record("live-1"), Record("live-1"), Record("live-2")]);

        var (total, items) = await repository.GetPageAsync(123456, 1, 100);
        Assert.Equal(2, total);
        Assert.Equal(2, items.Select(i => i.LiveId).Distinct().Count());
    }

    [Fact]
    public async Task UpsertMany_RepeatedSync_UpdatesExisting()
    {
        var repository = new LiveSessionRepository(fixture.Database);

        await repository.UpsertManyAsync([Record("live-1")]);
        await repository.UpsertManyAsync([Record("live-1"), Record("live-3")]);

        var (total, items) = await repository.GetPageAsync(123456, 1, 100);
        Assert.Equal(2, total);
        Assert.Equal(2, items.Select(i => i.LiveId).Distinct().Count());
    }

    [Fact]
    public async Task GetPage_ReturnsStartDateDescending()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var liveIds = new[] { "live-1", "live-2", "live-3", "live-4", "live-5" };

        for (var i = 0; i < liveIds.Length; i++)
        {
            await repository.UpsertManyAsync([Record(liveIds[i], baseDate.AddHours(i))]);
        }

        var (total, items) = await repository.GetPageAsync(123456, 1, 100);

        Assert.Equal(5, total);
        Assert.Equal(["live-5", "live-4", "live-3", "live-2", "live-1"], items.Select(i => i.LiveId).ToArray());
    }

    [Fact]
    public async Task GetPage_PaginatesWithoutOverlapOrGap()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 5; i++)
        {
            await repository.UpsertManyAsync([Record($"live-{i}", baseDate.AddHours(i))]);
        }

        var (total, page1) = await repository.GetPageAsync(123456, 1, 2);
        var (_, page2) = await repository.GetPageAsync(123456, 2, 2);
        var (_, page3) = await repository.GetPageAsync(123456, 3, 2);

        Assert.Equal(5, total);
        Assert.Equal(["live-5", "live-4"], page1.Select(i => i.LiveId).ToArray());
        Assert.Equal(["live-3", "live-2"], page2.Select(i => i.LiveId).ToArray());
        Assert.Equal(["live-1"], page3.Select(i => i.LiveId).ToArray());
    }

    [Fact]
    public async Task GetPage_EmptyRoom_ReturnsEmpty()
    {
        var repository = new LiveSessionRepository(fixture.Database);

        var (total, items) = await repository.GetPageAsync(999999, 1, 20);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task GetPage_TimeRange_FiltersByStartDateInclusive()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 4; i++)
        {
            await repository.UpsertManyAsync([Record($"live-{i}", baseDate.AddHours(i))]);
        }

        var (total, items) = await repository.GetPageAsync(123456, 1, 100, baseDate.AddHours(2), baseDate.AddHours(3));

        Assert.Equal(2, total);
        Assert.Equal(["live-3", "live-2"], items.Select(i => i.LiveId).ToArray());
    }

    [Fact]
    public async Task Upsert_SingleRecord_InsertsAndUpdates()
    {
        var repository = new LiveSessionRepository(fixture.Database);

        await repository.UpsertAsync(Record("live-1"));
        await repository.UpsertAsync(Record("live-1", DateTime.UtcNow.AddHours(2)));

        var (total, items) = await repository.GetPageAsync(123456, 1, 100);
        Assert.Equal(1, total);
        Assert.Equal("live-1", items[0].LiveId);
    }

    [Fact]
    public async Task CloseAsync_MarksFinished_IsIdempotent()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var startDate = DateTime.UtcNow;
        await repository.UpsertAsync(Record("live-1", startDate));

        var stopDate = startDate.AddHours(1);
        await repository.CloseAsync("live-1", stopDate);
        await repository.CloseAsync("live-1", stopDate.AddHours(1));

        var (total, items) = await repository.GetPageAsync(123456, 1, 100);
        Assert.Equal(1, total);
        Assert.True(items[0].IsFinish);
        Assert.Equal(TruncateToMs(stopDate), TruncateToMs(items[0].StopDate!.Value));
    }

    private static DateTime TruncateToMs(DateTime value)
    {
        var ms = value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond);
        return new DateTime(ms, DateTimeKind.Utc);
    }

    [Fact]
    public async Task FindMergeCandidates_ReturnsUnfinishedOrNearbySessions()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var ongoing = Record("ongoing", baseDate);
        ongoing.IsFinish = false;
        var finishedFar = Record("finished-far", baseDate.AddDays(-3));
        finishedFar.IsFinish = true;
        var finishedNear = Record("finished-near", baseDate.AddMinutes(3));
        finishedNear.IsFinish = true;
        await repository.UpsertAsync(ongoing);
        await repository.UpsertAsync(finishedFar);
        await repository.UpsertAsync(finishedNear);

        var candidates = await repository.FindMergeCandidatesAsync(123456, baseDate, TimeSpan.FromMinutes(10));

        var ids = candidates.Select(c => c.LiveId).ToHashSet();
        Assert.Contains("ongoing", ids);
        Assert.Contains("finished-near", ids);
        Assert.DoesNotContain("finished-far", ids);
    }
}
