using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveStatusRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static LiveStatusRecord Record(DateTime createdAt) => new()
    {
        RoomId = 123456,
        Title = "title",
        LiveStatus = true,
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task GetPage_ReturnsCreatedAtDescending()
    {
        var repository = new LiveStatusRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await repository.InsertAsync(Record(baseDate.AddHours(2)));
        await repository.InsertAsync(Record(baseDate.AddHours(5)));
        await repository.InsertAsync(Record(baseDate.AddHours(1)));
        await repository.InsertAsync(Record(baseDate.AddHours(4)));
        await repository.InsertAsync(Record(baseDate.AddHours(3)));

        var (total, items) = await repository.GetPageAsync(123456, 1, 100);

        Assert.Equal(5, total);
        Assert.Equal(
            new[] { 5, 4, 3, 2, 1 },
            items.Select(i => (int)(i.CreatedAt - baseDate).TotalHours).ToArray());
    }

    [Fact]
    public async Task GetPage_TimeRange_FiltersByCreatedAtInclusive()
    {
        var repository = new LiveStatusRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var h in new[] { 1, 2, 3, 4 })
        {
            await repository.InsertAsync(Record(baseDate.AddHours(h)));
        }

        var start = baseDate.AddHours(2);
        var end = baseDate.AddHours(3);
        var (total, items) = await repository.GetPageAsync(123456, 1, 100, start, end);

        Assert.Equal(2, total);
        Assert.Equal([3, 2], items.Select(i => (int)(i.CreatedAt - baseDate).TotalHours).ToArray());
    }
}
