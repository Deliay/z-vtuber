using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class WeiboTimelineRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    private WeiboTimelineRepository CreateRepository() => new(fixture.Database);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static WeiboTimelineRecord Record(string mblogId, long? pubTs = null) => new()
    {
        Uid = 2147483647,
        MblogId = mblogId,
        Data = $@"{{""id"":""{mblogId}""}}",
        PubTs = pubTs,
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task BulkUpsert_DeduplicatesByMblogId()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([Record("1"), Record("2")]);
        await repository.BulkUpsertAsync([Record("1"), Record("3")]);

        var (total, items) = await repository.GetPageAsync(2147483647, 1, 100);
        Assert.Equal(3, total);
        Assert.Equal(3, items.Select(i => i.MblogId).Distinct().Count());
    }

    [Fact]
    public async Task GetExistingMblogIds_ReturnsOnlyExisting()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([Record("1"), Record("2")]);

        var existing = await repository.GetExistingMblogIdsAsync(["1", "3", "4"]);

        Assert.Equal(new HashSet<string> { "1" }, existing);
    }

    [Fact]
    public async Task GetPage_SortsByPubTsDescending()
    {
        var repository = CreateRepository();
        foreach (var entry in new[] { (id: "2", ts: (long?)2000), (id: "1", ts: 1000L), (id: "3", ts: 3000L) })
        {
            await repository.BulkUpsertAsync([Record(entry.id, entry.ts)]);
        }

        var (total, items) = await repository.GetPageAsync(2147483647, 1, 100);

        Assert.Equal(3, total);
        Assert.Equal(["3", "2", "1"], items.Select(i => i.MblogId).ToArray());
    }

    [Fact]
    public async Task GetPage_PaginatesInDescendingOrder()
    {
        var repository = CreateRepository();
        for (var i = 1; i <= 5; i++)
        {
            await repository.BulkUpsertAsync([Record($"{i}", i * 1000L)]);
        }

        var (total, page1) = await repository.GetPageAsync(2147483647, 1, 2);
        var (_, page3) = await repository.GetPageAsync(2147483647, 3, 2);

        Assert.Equal(5, total);
        Assert.Equal(["5", "4"], page1.Select(i => i.MblogId).ToArray());
        Assert.Equal(["1"], page3.Select(i => i.MblogId).ToArray());
    }

    [Fact]
    public async Task GetPage_TimeRange_FiltersByPubTsInclusive()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([
            Record("1", 1000L),
            Record("2", 2000L),
            Record("3", 3000L),
            Record("4", 4000L),
        ]);

        var start = DateTimeOffset.FromUnixTimeSeconds(2000).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeSeconds(3000).UtcDateTime;
        var (total, items) = await repository.GetPageAsync(2147483647, 1, 100, start, end);

        Assert.Equal(2, total);
        Assert.Equal(["3", "2"], items.Select(i => i.MblogId).ToArray());
    }

    [Fact]
    public async Task GetPage_TimeRange_ExcludesNullPubTs()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([
            Record("1", 1000L),
            Record("2", null),
            Record("3", 2000L),
        ]);

        var start = DateTimeOffset.FromUnixTimeSeconds(500).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeSeconds(2500).UtcDateTime;
        var (total, items) = await repository.GetPageAsync(2147483647, 1, 100, start, end);

        Assert.Equal(2, total);
        Assert.Equal(["3", "1"], items.Select(i => i.MblogId).ToArray());
    }

    [Fact]
    public async Task GetPage_TimeRange_SingleSidedBoundary()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([
            Record("1", 1000L),
            Record("2", 2000L),
            Record("3", 3000L),
        ]);

        var start = DateTimeOffset.FromUnixTimeSeconds(2000).UtcDateTime;
        var (total, items) = await repository.GetPageAsync(2147483647, 1, 100, startedAt: start);

        Assert.Equal(2, total);
        Assert.Equal(["3", "2"], items.Select(i => i.MblogId).ToArray());
    }
}
