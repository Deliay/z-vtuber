using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Pool;

namespace Z.Vtuber.Tests;

public class WeiboCookieDispatchTests(MongoDbFixture db) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private IMongoCollection<CookieRecord> WeiboCollection =>
        db.Database.GetCollection<CookieRecord>(MongoCollectionNames.WeiboCookies);

    private IMongoCollection<CookieRecord> BilibiliCollection =>
        db.Database.GetCollection<CookieRecord>(MongoCollectionNames.Cookies);

    private CookieDispatchService CreateService() => new(
        db.Database, MongoCollectionNames.WeiboCookies, MongoCollectionNames.WeiboCookieSessions);

    private WeiboCookieDispatchController CreateController(CookieDispatchService dispatch) => new(
        dispatch,
        new FakeInvalidationService(),
        NullLogger<WeiboCookieDispatchController>.Instance);

    private async Task<CookieRecord> InsertWeiboCookieAsync(string status = CookieStatus.Active)
    {
        var record = new CookieRecord
        {
            Cookie = $"weibo-cookie-{Guid.NewGuid():N}",
            Status = status,
        };
        await WeiboCollection.InsertOneAsync(record);
        return record;
    }

    [Fact]
    public async Task Checkout_ReadsOnlyWeiboCollection()
    {
        var weibo = await InsertWeiboCookieAsync();
        await BilibiliCollection.InsertOneAsync(new CookieRecord { Cookie = "bili-cookie", Status = CookieStatus.Active });

        var controller = CreateController(CreateService());

        var result = await controller.Checkout(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<CookieCheckoutResponse>(okResult.Value);
        Assert.Equal(weibo.Id.ToString(), response.CookieId);
        Assert.Equal(weibo.Cookie, response.Cookie);
    }

    [Fact]
    public async Task Checkout_NoWeiboCookie_Returns404EvenIfBilibiliHasCookie()
    {
        await BilibiliCollection.InsertOneAsync(new CookieRecord { Cookie = "bili-cookie", Status = CookieStatus.Active });

        var controller = CreateController(CreateService());

        var result = await controller.Checkout(CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_CooldownCookie_NotReturned()
    {
        var active = await InsertWeiboCookieAsync();
        await InsertWeiboCookieAsync(CookieStatus.Offline);

        var controller = CreateController(CreateService());

        var result = await controller.Checkout(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<CookieCheckoutResponse>(okResult.Value);
        Assert.Equal(active.Id.ToString(), response.CookieId);
    }

    [Fact]
    public async Task ListAvailable_ReturnsWeiboCookiesWithoutRawCookie()
    {
        await InsertWeiboCookieAsync();

        var controller = CreateController(CreateService());

        var result = await controller.ListAvailable(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value?.GetType().GetProperty("items")?.GetValue(okResult.Value) as System.Collections.IList;
        Assert.NotNull(items);
        Assert.Single(items!);
    }

    [Fact]
    public async Task SetCooldown_WeiboCookie_UpdatesCooldown()
    {
        var cookie = await InsertWeiboCookieAsync();

        var controller = CreateController(CreateService());

        var result = await controller.SetCooldown(new CooldownRequest
        {
            CookieId = cookie.Id.ToString(),
            DurationSeconds = 300,
        }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        var updated = await WeiboCollection.Find(c => c.Id == cookie.Id).FirstOrDefaultAsync();
        Assert.NotNull(updated!.CooldownUntil);
    }

    [Fact]
    public async Task SetCooldown_UnknownCookie_Returns404()
    {
        var controller = CreateController(CreateService());

        var result = await controller.SetCooldown(new CooldownRequest
        {
            CookieId = "invalid-id",
            DurationSeconds = 300,
        }, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
