using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class AccountBasicInfoRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static AccountBasicInfoRecord Record(string data) => new()
    {
        Mid = 1150976664,
        RoomId = 123456,
        Data = data,
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Upsert_ExistingRecord_UpdatesWithoutThrowing()
    {
        var repository = new AccountBasicInfoRepository(fixture.Database);
        await repository.UpsertAsync(Record("data-1"));

        await repository.UpsertAsync(Record("data-2"));

        var latest = await repository.GetLatestByMidAsync(1150976664);
        Assert.NotNull(latest);
        Assert.Equal("data-2", latest.Data);
    }
}
