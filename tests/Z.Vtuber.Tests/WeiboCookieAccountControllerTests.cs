using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;

namespace Z.Vtuber.Tests;

public class WeiboCookieAccountControllerTests
{
    private readonly FakeDistributedLockProvider _lockProvider = new();
    private readonly FakeBrowserSessionManager _browserSessionManager = new();

    private WeiboCookieAccountController CreateController(
        ICookieSessionRepository? sessionRepository = null,
        ICookiePoolService? cookiePool = null,
        FakeCookieInvalidEventPublisher? eventPublisher = null,
        CookieServiceOptions? options = null)
    {
        return new WeiboCookieAccountController(
            sessionRepository ?? new FakeCookieSessionRepository(),
            _browserSessionManager,
            cookiePool ?? new FakeCookiePoolService(),
            _lockProvider,
            eventPublisher ?? new FakeCookieInvalidEventPublisher(),
            Options.Create(options ?? new CookieServiceOptions { MaxPendingLoginSessions = 3 }),
            NullLogger<WeiboCookieAccountController>.Instance);
    }

    [Fact]
    public async Task CreateAccount_Returns202WithQrCode()
    {
        var controller = CreateController();

        var result = await controller.CreateAccount(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
        var response = Assert.IsAssignableFrom<PendingAccountResponse>(objectResult.Value);
        Assert.Equal("pending_login", response.Status);
        Assert.NotEmpty(response.QrCodeImage);
        Assert.Contains(response.AccountId, _browserSessionManager.CreatedSessions);
    }

    [Fact]
    public async Task CreateAccount_ExceedsPendingLimit_Returns429()
    {
        var repo = new FakeCookieSessionRepository();
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "pending_login" });
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "pending_login" });
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "pending_login" });

        var controller = CreateController(sessionRepository: repo, options: new CookieServiceOptions { MaxPendingLoginSessions = 3 });

        var result = await controller.CreateAccount(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetAccount_Found_ReturnsStatus()
    {
        var repo = new FakeCookieSessionRepository();
        var id = ObjectId.GenerateNewId();
        repo.Sessions.Add(new CookieSessionRecord { Id = id, Status = "active", Mid = 998877, Uname = "微博用户" });
        var controller = CreateController(sessionRepository: repo);

        var result = await controller.GetAccount(id.ToString(), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<AccountStatusResponse>(okResult.Value);
        Assert.Equal("active", response.Status);
        Assert.Equal(998877L, response.Mid);
        Assert.Equal("微博用户", response.Uname);
    }

    [Fact]
    public async Task GetAccount_NotFound_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.GetAccount(ObjectId.GenerateNewId().ToString(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ListAccounts_ReturnsWeiboPlatform()
    {
        var repo = new FakeCookieSessionRepository();
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "active", Mid = 1, Uname = "user1" });

        var controller = CreateController(sessionRepository: repo);

        var result = await controller.ListAccounts(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic value = okResult.Value!;
        var items = (System.Collections.IEnumerable)value.items;
        var first = Assert.IsType<AccountItem>(items.Cast<object>().First());
        Assert.Equal("weibo", first.Platform);
    }

    [Fact]
    public async Task DeleteAccount_Existing_OfflinesCookieAndPublishesEvent()
    {
        var repo = new FakeCookieSessionRepository();
        var pool = new MockCookiePoolService();
        pool.FindByCookieIdFunc = _ => new CookieRecord { Cookie = "weibo-cookie-value", Status = CookieStatus.Active };
        var publisher = new FakeCookieInvalidEventPublisher();
        var id = ObjectId.GenerateNewId();
        repo.Sessions.Add(new CookieSessionRecord { Id = id, Status = "active", CookieId = "cookie-abc" });
        var controller = CreateController(sessionRepository: repo, cookiePool: pool, eventPublisher: publisher);

        var result = await controller.DeleteAccount(id.ToString(), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("weibo-cookie-value", pool.OfflineCalls);
        Assert.Contains(id.ToString(), _browserSessionManager.DeletedSessions);
        Assert.Single(publisher.PublishedEvents);
        Assert.Equal("account_removed", publisher.PublishedEvents[0].Reason);
    }
}
