using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Pool;

namespace Z.Vtuber.Tests;

public class CookieDispatchTests(MongoDbFixture db) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private IMongoCollection<CookieRecord> Collection =>
        db.Database.GetCollection<CookieRecord>(MongoCollectionNames.Cookies);

    private CookieDispatchService CreateService() => new(db.Database);

    private async Task<CookieRecord> InsertCookieAsync(string status = CookieStatus.Active, DateTime? cooldownUntil = null)
    {
        var record = new CookieRecord
        {
            Cookie = $"fake-cookie-{Guid.NewGuid():N}",
            Status = status,
            CooldownUntil = cooldownUntil,
        };
        await Collection.InsertOneAsync(record);
        return record;
    }

    [Fact]
    public async Task Checkout_RoundRobin_ByLastUsedAt()
    {
        var cookie1 = await InsertCookieAsync();
        var cookie2 = await InsertCookieAsync();
        var cookie3 = await InsertCookieAsync();

        var service = CreateService();

        var result1 = await service.CheckoutAsync();
        Assert.Equal(cookie1.Id.ToString(), result1.CookieId);

        var result2 = await service.CheckoutAsync();
        Assert.Equal(cookie2.Id.ToString(), result2.CookieId);

        var result3 = await service.CheckoutAsync();
        Assert.Equal(cookie3.Id.ToString(), result3.CookieId);
    }

    [Fact]
    public async Task Checkout_CooldownCookie_NotReturned()
    {
        var active = await InsertCookieAsync();
        await InsertCookieAsync(cooldownUntil: DateTime.UtcNow.AddMinutes(5));

        var service = CreateService();

        var result = await service.CheckoutAsync();
        Assert.Equal(active.Id.ToString(), result.CookieId);
    }

    [Fact]
    public async Task Checkout_NoAvailable_ThrowsNoAvailableCookieException()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<NoAvailableCookieException>(() => service.CheckoutAsync());
    }

    [Fact]
    public async Task SetCooldown_UpdatesCooldownUntil()
    {
        var cookie = await InsertCookieAsync();
        var service = CreateService();

        await service.SetCooldownAsync(cookie.Id.ToString(), TimeSpan.FromMinutes(5));

        var updated = await Collection.Find(c => c.Id == cookie.Id).FirstOrDefaultAsync();
        Assert.NotNull(updated);
        Assert.NotNull(updated.CooldownUntil);
        Assert.True(updated.CooldownUntil > DateTime.UtcNow.AddMinutes(4));
    }

    [Fact]
    public async Task SetCooldown_InvalidCookieId_DoesNotThrow()
    {
        var service = CreateService();

        await service.SetCooldownAsync("invalid-id", TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task GetAvailable_ReturnsActiveNonCooldownCookies()
    {
        var active = await InsertCookieAsync();
        await InsertCookieAsync(CookieStatus.Offline);
        await InsertCookieAsync(cooldownUntil: DateTime.UtcNow.AddMinutes(5));

        var service = CreateService();

        var items = await service.GetAvailableAsync();
        Assert.Single(items);
        Assert.Equal(active.Id.ToString(), items[0].CookieId);
    }

    [Fact]
    public async Task GetAvailable_DoesNotReturnCookie原文()
    {
        var cookie = await InsertCookieAsync();
        var service = CreateService();

        var items = await service.GetAvailableAsync();
        Assert.Single(items);
        // CookieItem 不包含 Cookie 属性（原文不暴露），编译器即保证
    }
}
