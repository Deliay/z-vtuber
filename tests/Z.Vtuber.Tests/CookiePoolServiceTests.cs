using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class CookiePoolServiceTests(MongoDbFixture fixture) : IAsyncLifetime
{
    private CookiePoolService CreateService() => new(fixture.Database);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Next_RoundRobinsAcrossActiveCookies()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");

        var first = await service.NextAsync();
        var second = await service.NextAsync();
        var third = await service.NextAsync();

        Assert.Equal("cookie-a", first.Cookie);
        Assert.Equal("cookie-b", second.Cookie);
        Assert.Equal("cookie-a", third.Cookie);
    }

    [Fact]
    public async Task AddAsync_IsIdempotent()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-a");

        var active = await service.GetActiveAsync();
        Assert.Single(active);
    }

    [Fact]
    public async Task AddAsync_ReturnsRecordWithId_AndPreservesSameIdOnDuplicate()
    {
        var service = CreateService();
        var first = await service.AddAsync("cookie-a");
        var second = await service.AddAsync("cookie-a");

        Assert.NotEqual(MongoDB.Bson.ObjectId.Empty, first.Id);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task OfflineCookie_IsNotSelected()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");
        await service.MarkOfflineAsync("cookie-a");

        var next = await service.NextAsync();
        Assert.Equal("cookie-b", next.Cookie);
    }

    [Fact]
    public async Task NoActiveCookie_ThrowsObservableException()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<NoAvailableCookieException>(() => service.NextAsync());
    }

    [Fact]
    public async Task CooldownCookie_IsNotAssignedByNext()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");
        await service.MarkCooldownAsync("cookie-a", TimeSpan.FromMinutes(30));

        var next = await service.NextAsync();
        Assert.Equal("cookie-b", next.Cookie);
    }

    [Fact]
    public async Task CooldownCookie_BecomesAvailableAfterExpiry()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");

        // set cooldown to 1 second from now, then wait
        await service.MarkCooldownAsync("cookie-a", TimeSpan.FromSeconds(1));
        var during = await service.NextAsync();
        Assert.Equal("cookie-b", during.Cookie);

        await Task.Delay(TimeSpan.FromSeconds(1));
        var after = await service.NextAsync();
        Assert.Equal("cookie-a", after.Cookie);
    }

    [Fact]
    public async Task LegacyDocument_WithoutCooldownUntil_IsIncludedInRotation()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");

        // Simulate legacy document: directly set CooldownUntil to null via raw update
        var collection = fixture.Database.GetCollection<MongoDB.Bson.BsonDocument>(
            Persistence.MongoCollectionNames.Cookies);
        var filter = new MongoDB.Bson.BsonDocument("cookie", "cookie-a");
        var update = new MongoDB.Bson.BsonDocument("$set",
            new MongoDB.Bson.BsonDocument("cooldownUntil",
                MongoDB.Bson.BsonNull.Value));
        await collection.UpdateOneAsync(filter, update);

        var next = await service.NextAsync();
        Assert.Equal("cookie-a", next.Cookie);
    }

    [Fact]
    public async Task GetActiveAsync_ExcludesCooldownCookies()
    {
        var service = CreateService();
        await service.AddAsync("cookie-a");
        await service.AddAsync("cookie-b");
        await service.MarkCooldownAsync("cookie-a", TimeSpan.FromMinutes(30));

        var active = await service.GetActiveAsync();
        Assert.Single(active);
        Assert.Equal("cookie-b", active[0].Cookie);
    }
}
