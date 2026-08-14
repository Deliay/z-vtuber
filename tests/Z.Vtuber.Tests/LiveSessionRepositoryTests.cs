using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveSessionRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static LiveSessionRecord Record(string liveId) => new()
    {
        LiveId = liveId,
        RoomId = 123456,
        Title = "title",
        StartDate = DateTime.UtcNow,
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task UpsertMany_DuplicatesInBatch_AreDeduplicated()
    {
        var repository = new LiveSessionRepository(fixture.Database);

        await repository.UpsertManyAsync([Record("live-1"), Record("live-1"), Record("live-2")]);

        var items = await repository.GetByRoomIdAsync(123456, 1, 100);
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Select(i => i.LiveId).Distinct().Count());
    }

    [Fact]
    public async Task UpsertMany_RepeatedSync_UpdatesExisting()
    {
        var repository = new LiveSessionRepository(fixture.Database);

        await repository.UpsertManyAsync([Record("live-1")]);
        await repository.UpsertManyAsync([Record("live-1"), Record("live-3")]);

        var items = await repository.GetByRoomIdAsync(123456, 1, 100);
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Select(i => i.LiveId).Distinct().Count());
    }
}
