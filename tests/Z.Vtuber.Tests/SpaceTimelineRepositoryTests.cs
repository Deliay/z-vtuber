using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class SpaceTimelineRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    private SpaceTimelineRepository CreateRepository() => new(fixture.Database);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static SpaceTimelineRecord Record(string dynamicId) => new()
    {
        Mid = 1150976664,
        DynamicId = dynamicId,
        Data = $@"{{""id_str"":""{dynamicId}""}}",
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task BulkUpsert_DeduplicatesByDynamicId()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([Record("1"), Record("2")]);
        await repository.BulkUpsertAsync([Record("1"), Record("3")]);

        var (total, items) = await repository.GetPageAsync(1150976664, 1, 100);
        Assert.Equal(3, total);
        Assert.Equal(3, items.Select(i => i.DynamicId).Distinct().Count());
    }

    [Fact]
    public async Task GetExistingDynamicIds_ReturnsOnlyExisting()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([Record("1"), Record("2")]);

        var existing = await repository.GetExistingDynamicIdsAsync(["1", "3", "4"]);

        Assert.Equal(new HashSet<string> { "1" }, existing);
    }

    [Fact]
    public async Task GetPage_SortsByDynamicIdNumericDescending()
    {
        var repository = CreateRepository();
        foreach (var id in new[] { "2", "100", "9", "99", "10" })
        {
            await repository.BulkUpsertAsync([Record(id)]);
        }

        var (total, items) = await repository.GetPageAsync(1150976664, 1, 100);

        Assert.Equal(5, total);
        Assert.Equal(["100", "99", "10", "9", "2"], items.Select(i => i.DynamicId).ToArray());
    }

    [Fact]
    public async Task GetPage_PaginatesInDescendingOrder()
    {
        var repository = CreateRepository();
        for (var i = 1; i <= 5; i++)
        {
            await repository.BulkUpsertAsync([Record($"{i}")]);
        }

        var (total, page1) = await repository.GetPageAsync(1150976664, 1, 2);
        var (_, page3) = await repository.GetPageAsync(1150976664, 3, 2);

        Assert.Equal(5, total);
        Assert.Equal(["5", "4"], page1.Select(i => i.DynamicId).ToArray());
        Assert.Equal(["1"], page3.Select(i => i.DynamicId).ToArray());
    }
}
