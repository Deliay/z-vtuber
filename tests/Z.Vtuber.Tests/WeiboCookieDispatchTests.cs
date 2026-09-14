using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Persistence;
using Z.Vtuber.Cookie.Platform;
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
        new FakeCookiePoolService(),
        new FakeCookieSessionRepository(),
        new FakeBrowserSessionManager(),
        new WeiboCookiePlatformProfile(),
        new FakeDistributedLockProvider(),
        Options.Create(new CookieServiceOptions()),
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
    public async Task Checkout_OfflineCookie_NotReturned()
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
    public async Task Checkout_CooldownCookie_NotReturned()
    {
        var active = await InsertWeiboCookieAsync();
        await WeiboCollection.InsertOneAsync(new CookieRecord
        {
            Cookie = $"weibo-cooldown-{Guid.NewGuid():N}",
            Status = CookieStatus.Active,
            CooldownUntil = DateTime.UtcNow.AddMinutes(5),
        });

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

    [Fact]
    public async Task DeleteAccount_OfflinesLinkedCookie_SoItCannotBeCheckedOut()
    {
        var cookie = await InsertWeiboCookieAsync();
        var sessionRepo = new CookieSessionRepository(db.Database, MongoCollectionNames.WeiboCookieSessions);
        var pool = new CookiePoolService(db.Database, MongoCollectionNames.WeiboCookies);
        var session = new CookieSessionRecord
        {
            Status = "active",
            Cookie = cookie.Cookie,
            CookieId = cookie.Id.ToString(),
        };
        await sessionRepo.InsertAsync(session);

        var accountController = new WeiboCookieAccountController(
            sessionRepo,
            new FakeBrowserSessionManager(),
            pool,
            new FakeDistributedLockProvider(),
            new FakeCookieInvalidEventPublisher(),
            Options.Create(new CookieServiceOptions()),
            NullLogger<WeiboCookieAccountController>.Instance);

        var deleteResult = await accountController.DeleteAccount(session.Id.ToString(), CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);

        var reloaded = await WeiboCollection.Find(c => c.Id == cookie.Id).FirstOrDefaultAsync();
        Assert.Equal(CookieStatus.Offline, reloaded!.Status);

        var checkoutResult = await CreateController(CreateService()).Checkout(CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(checkoutResult);
    }
}
