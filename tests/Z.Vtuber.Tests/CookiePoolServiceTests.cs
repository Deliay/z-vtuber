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
}
