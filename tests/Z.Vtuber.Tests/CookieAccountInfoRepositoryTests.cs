using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class CookieAccountInfoRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static CookieAccountInfoRecord Record(ObjectId cookieId, string? uname, DateTime fetchedAt) => new()
    {
        CookieId = cookieId,
        Mid = 10086,
        Uname = uname,
        FetchedAt = fetchedAt,
    };

    [Fact]
    public async Task Upsert_InsertThenUpdate_DoesNotDuplicate()
    {
        var repository = new CookieAccountInfoRepository(fixture.Database);
        var cookieId = ObjectId.GenerateNewId();

        await repository.UpsertAsync(Record(cookieId, "first", DateTime.UtcNow));
        await repository.UpsertAsync(Record(cookieId, "second", DateTime.UtcNow));

        var found = await repository.GetByCookieIdsAsync([cookieId]);
        Assert.Single(found);
        Assert.Equal("second", found[cookieId].Uname);
    }

    [Fact]
    public async Task GetByCookieIdsAsync_ReturnsOnlyHitCookieIds()
    {
        var repository = new CookieAccountInfoRepository(fixture.Database);
        var cookieA = ObjectId.GenerateNewId();
        var cookieB = ObjectId.GenerateNewId();
        var missing = ObjectId.GenerateNewId();

        await repository.UpsertAsync(Record(cookieA, "a", DateTime.UtcNow));
        await repository.UpsertAsync(Record(cookieB, "b", DateTime.UtcNow));

        var found = await repository.GetByCookieIdsAsync([cookieA, missing]);

        Assert.Single(found);
        Assert.True(found.ContainsKey(cookieA));
        Assert.False(found.ContainsKey(cookieB));
        Assert.False(found.ContainsKey(missing));
    }

    [Fact]
    public async Task GetByCookieIdsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var repository = new CookieAccountInfoRepository(fixture.Database);
        var found = await repository.GetByCookieIdsAsync([]);
        Assert.Empty(found);
    }
}