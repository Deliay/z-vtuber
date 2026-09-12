using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Pool;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Tests;

public class CookieReportInvalidTests
{
    private readonly FakeDistributedLockProvider _lockProvider = new();
    private readonly FakeBrowserSessionManager _browserSessionManager = new();

    private CookieDispatchController CreateController(
        ICookieSessionRepository? sessionRepository = null,
        ICookiePoolService? cookiePool = null,
        IInvalidationService? invalidationService = null,
        CookieServiceOptions? options = null)
    {
        return new CookieDispatchController(
            new FakeCookieDispatchService(),
            invalidationService ?? new FakeInvalidationService(),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<CookieDispatchController>());
    }

    [Fact]
    public async Task ReportInvalid_RefreshSuccess_Returns200WithNewCookie()
    {
        var fakeService = new FakeInvalidationService();
        fakeService.NextResult = new InvalidationResultValue
        {
            Result = InvalidationResult.Refreshed,
            NewCookieId = "new-cookie-id",
            NewCookie = "SESSDATA=new_value",
            AccountId = "acc-123",
        };
        var controller = CreateController(invalidationService: fakeService);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "old-cookie-id" },
            CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<CookieCheckoutResponse>(okResult.Value);
        Assert.True(response.Refreshed);
        Assert.Equal("new-cookie-id", response.CookieId);
        Assert.Equal("SESSDATA=new_value", response.Cookie);
        Assert.Equal("acc-123", response.AccountId);
    }

    [Fact]
    public async Task ReportInvalid_DedupShortCircuit_ReturnsRefreshed()
    {
        var repo = new FakeCookieSessionRepository();
        var sessionId = ObjectId.GenerateNewId();
        repo.Sessions.Add(new CookieSessionRecord
        {
            Id = sessionId,
            Status = "active",
            Cookie = "current-cookie",
            CookieId = "cookie-id-1",
            LastRefreshAt = DateTime.UtcNow.AddSeconds(-5),
        });

        var fakeService = new FakeInvalidationService();
        fakeService.NextResult = new InvalidationResultValue
        {
            Result = InvalidationResult.Refreshed,
            AccountId = "acc-123",
        };
        var controller = CreateController(invalidationService: fakeService);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "cookie-id-1" },
            CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<CookieCheckoutResponse>(okResult.Value);
        Assert.True(response.Refreshed);
    }

    [Fact]
    public async Task ReportInvalid_Timeout_Returns504()
    {
        var fakeService = new FakeInvalidationService();
        fakeService.NextResult = new InvalidationResultValue
        {
            Result = InvalidationResult.Timeout,
        };
        var controller = CreateController(invalidationService: fakeService);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "some-cookie-id" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(504, objectResult.StatusCode);
    }

    [Fact]
    public async Task ReportInvalid_SessionInvalid_Returns410WithAccountId()
    {
        var fakeService = new FakeInvalidationService();
        fakeService.NextResult = new InvalidationResultValue
        {
            Result = InvalidationResult.SessionInvalid,
            AccountId = "acc-456",
        };
        var controller = CreateController(invalidationService: fakeService);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "cookie-id-2" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, objectResult.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(objectResult.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("acc-456", doc.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("session_invalid", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReportInvalid_NotFound_Returns404()
    {
        var fakeService = new FakeInvalidationService();
        fakeService.NextResult = new InvalidationResultValue
        {
            Result = InvalidationResult.NotFound,
        };
        var controller = CreateController(invalidationService: fakeService);

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "nonexistent" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ReportInvalid_EmptyCookieId_Returns400()
    {
        var controller = CreateController();

        var result = await controller.ReportInvalid(
            new ReportInvalidRequest { CookieId = "" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task InvalidationService_ShortCircuitAfterRecentRefresh_ReturnsRefreshed()
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
            LastRefreshAt = DateTime.UtcNow.AddSeconds(-5),
        });

        var cookiePool = new FakeCookiePoolService();
        var eventPublisher = new FakeCookieInvalidEventPublisher();
        var service = new InvalidationService(
            repo,
            cookiePool,
            _lockProvider,
            _browserSessionManager,
            eventPublisher,
            Options.Create(new CookieServiceOptions
            {
                ReportInvalidTimeoutSeconds = 60,
                LockAcquireWaitMs = 5000,
            }),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<InvalidationService>());

        var result = await service.ReportInvalidAsync(cookieId, CancellationToken.None);

        Assert.Equal(InvalidationResult.Refreshed, result.Result);
        Assert.Empty(_browserSessionManager.RefreshedSessions);
    }
}

public class FakeInvalidationService : IInvalidationService
{
    public InvalidationResultValue NextResult { get; set; } = new InvalidationResultValue { Result = InvalidationResult.Refreshed };

    public Task<InvalidationResultValue> ReportInvalidAsync(string cookieId, CancellationToken ct = default)
    {
        return Task.FromResult(NextResult);
    }
}

public class FakeCookieDispatchService : ICookieDispatchService
{
    public Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new CookieCheckoutResult { CookieId = "fake", Cookie = "fake" });
    }

    public Task<bool> SetCooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<CookieItem>> GetAvailableAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<CookieItem>>([]);
    }
}
