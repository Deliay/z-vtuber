using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Platform;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Tests;

public class WeiboCookieReportInvalidTests
{
    private readonly FakeBrowserSessionManager _browserSessionManager = new();

    private WeiboCookieDispatchController CreateController(IInvalidationService invalidationService) =>
        new(new FakeCookieDispatchService(), invalidationService, NullLogger<WeiboCookieDispatchController>.Instance);

    [Fact]
    public async Task ReportInvalid_RefreshSuccess_Returns200WithNewCookie()
    {
        var service = new FakeInvalidationService
        {
            NextResult = new InvalidationResultValue
            {
                Result = InvalidationResult.Refreshed,
                NewCookieId = "new-cookie-id",
                NewCookie = "SUB=new_value; SUBP=x",
                AccountId = "acc-weibo",
            },
        };
        var controller = CreateController(service);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "old-cookie-id" }, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<CookieCheckoutResponse>(okResult.Value);
        Assert.True(response.Refreshed);
        Assert.Equal("SUB=new_value; SUBP=x", response.Cookie);
    }

    [Fact]
    public async Task ReportInvalid_SessionInvalid_Returns410()
    {
        var service = new FakeInvalidationService
        {
            NextResult = new InvalidationResultValue { Result = InvalidationResult.SessionInvalid, AccountId = "acc-1" },
        };
        var controller = CreateController(service);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "cookie-2" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, objectResult.StatusCode);
    }

    [Fact]
    public async Task ReportInvalid_Timeout_Returns504()
    {
        var service = new FakeInvalidationService
        {
            NextResult = new InvalidationResultValue { Result = InvalidationResult.Timeout },
        };
        var controller = CreateController(service);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "cookie-3" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(504, objectResult.StatusCode);
    }

    [Fact]
    public async Task ReportInvalid_NotFound_Returns404()
    {
        var service = new FakeInvalidationService
        {
            NextResult = new InvalidationResultValue { Result = InvalidationResult.NotFound },
        };
        var controller = CreateController(service);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "nope" }, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task InvalidationService_WeiboProfile_UsesPlatformLockKey()
    {
        var sessionId = ObjectId.GenerateNewId();
        var cookieId = "test-cookie-id";
        var repo = new FakeCookieSessionRepository();
        repo.Sessions.Add(new CookieSessionRecord
        {
            Id = sessionId,
            Status = "active",
            Cookie = "old-cookie",
            CookieId = cookieId,
            LastRefreshAt = DateTime.MinValue,
        });

        var cookiePool = new FakeCookiePoolService { Cookie = "new-cookie" };
        var lockProvider = new FakeDistributedLockProvider();
        string? capturedKey = null;
        lockProvider.LockAcquired = key => capturedKey = key;

        var service = new InvalidationService(
            new WeiboCookiePlatformProfile(),
            repo,
            cookiePool,
            lockProvider,
            _browserSessionManager,
            new FakeCookieInvalidEventPublisher(),
            Options.Create(new CookieServiceOptions { ReportInvalidTimeoutSeconds = 60, LockAcquireWaitMs = 5000 }),
            NullLogger<InvalidationService>.Instance);

        var result = await service.ReportInvalidAsync(cookieId, CancellationToken.None);

        Assert.Equal(InvalidationResult.Refreshed, result.Result);
        Assert.NotNull(capturedKey);
        Assert.StartsWith($"lock:cookie-refresh:weibo:{sessionId}", capturedKey);
    }
}
