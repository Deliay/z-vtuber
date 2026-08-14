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
        await repository.BulkUpsertAsync([Record("dyn-1"), Record("dyn-2")]);
        await repository.BulkUpsertAsync([Record("dyn-1"), Record("dyn-3")]);

        var (total, items) = await repository.GetPageAsync(1150976664, 1, 100);
        Assert.Equal(3, total);
        Assert.Equal(3, items.Select(i => i.DynamicId).Distinct().Count());
    }

    [Fact]
    public async Task GetExistingDynamicIds_ReturnsOnlyExisting()
    {
        var repository = CreateRepository();
        await repository.BulkUpsertAsync([Record("dyn-1"), Record("dyn-2")]);

        var existing = await repository.GetExistingDynamicIdsAsync(["dyn-1", "dyn-3", "dyn-4"]);

        Assert.Equal(new HashSet<string> { "dyn-1" }, existing);
    }

    [Fact]
    public async Task GetPage_PaginatesInDescendingOrder()
    {
        var repository = CreateRepository();
        for (var i = 1; i <= 5; i++)
        {
            await repository.BulkUpsertAsync([Record($"dyn-{i}")]);
        }

        var (total, page1) = await repository.GetPageAsync(1150976664, 1, 2);
        var (_, page3) = await repository.GetPageAsync(1150976664, 3, 2);

        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);
        Assert.Single(page3);
    }
}
