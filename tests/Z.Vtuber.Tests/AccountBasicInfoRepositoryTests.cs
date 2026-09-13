using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class AccountBasicInfoRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
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

    [Fact]
    public async Task GetLatestByMidsAsync_MultiMidMultiVersion_ReturnsLatestPerMid()
    {
        var repository = new AccountBasicInfoRepository(fixture.Database);
        var early = DateTime.UtcNow.AddHours(-2);

        await repository.UpsertAsync(Record("m1-early"));
        await repository.UpsertAsync(Record("m1-late"));
        await repository.UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = 222,
            RoomId = 222,
            Data = "m2-data",
            FetchedAt = early,
        });

        var found = await repository.GetLatestByMidsAsync([1150976664, 222, 333]);

        Assert.Equal(2, found.Count);
        Assert.False(found.ContainsKey(333));
        Assert.Equal("m1-late", found[1150976664].Data);
        Assert.Equal("m2-data", found[222].Data);
    }

    [Fact]
    public async Task GetLatestByMidsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var repository = new AccountBasicInfoRepository(fixture.Database);
        var found = await repository.GetLatestByMidsAsync([]);
        Assert.Empty(found);
    }
}
