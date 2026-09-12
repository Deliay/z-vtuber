using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Controllers;
using Z.Vtuber.Cookie.Refresh;
using Z.Vtuber.Cookie.Persistence;

namespace Z.Vtuber.Tests;

public class CookieAccountControllerTests
{
    private readonly FakeDistributedLockProvider _lockProvider = new();
    private readonly FakeBrowserSessionManager _browserSessionManager = new();

    private CookieAccountController CreateController(
        ICookieSessionRepository? sessionRepository = null,
        ICookiePoolService? cookiePool = null,
        CookieServiceOptions? options = null)
    {
        return new CookieAccountController(
            sessionRepository ?? new FakeCookieSessionRepository(),
            _browserSessionManager,
            cookiePool ?? new FakeCookiePoolService(),
            _lockProvider,
            new FakeCookieInvalidEventPublisher(),
            Options.Create(options ?? new CookieServiceOptions { MaxPendingLoginSessions = 3 }),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<CookieAccountController>());
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
        Assert.True(_browserSessionManager.CreatedSessions.Count > 0);
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

        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetAccount_InvalidId_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.GetAccount("invalid-id", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetAccount_NotFound_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.GetAccount(ObjectId.GenerateNewId().ToString(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetAccount_Found_ReturnsStatus()
    {
        var repo = new FakeCookieSessionRepository();
        var id = ObjectId.GenerateNewId();
        repo.Sessions.Add(new CookieSessionRecord
        {
            Id = id,
            Status = "active",
            Mid = 12345,
            Uname = "test_user",
        });
        var controller = CreateController(sessionRepository: repo);

        var result = await controller.GetAccount(id.ToString(), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsAssignableFrom<AccountStatusResponse>(okResult.Value);
        Assert.Equal("active", response.Status);
        Assert.Equal(12345L, response.Mid);
        Assert.Equal("test_user", response.Uname);
    }

    [Fact]
    public async Task ListAccounts_ReturnsAllStatuses()
    {
        var repo = new FakeCookieSessionRepository();
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "pending_login" });
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "active", Mid = 1, Uname = "user1" });
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "expired" });
        repo.Sessions.Add(new CookieSessionRecord { Id = ObjectId.GenerateNewId(), Status = "invalid" });

        var controller = CreateController(sessionRepository: repo);

        var result = await controller.ListAccounts(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic value = okResult.Value!;
        var items = (System.Collections.IEnumerable)value.items;
        int count = 0;
        foreach (var _ in items) count++;
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task DeleteAccount_NonExistentId_ReturnsNoContent()
    {
        var controller = CreateController();

        var result = await controller.DeleteAccount("invalid-id", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteAccount_Existing_DeletesSession()
    {
        var repo = new FakeCookieSessionRepository();
        var id = ObjectId.GenerateNewId();
        repo.Sessions.Add(new CookieSessionRecord { Id = id, Status = "active" });
        var controller = CreateController(sessionRepository: repo);

        var result = await controller.DeleteAccount(id.ToString(), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains(id.ToString(), _browserSessionManager.DeletedSessions);
    }
}

public class FakeCookieSessionRepository : ICookieSessionRepository
{
    public List<CookieSessionRecord> Sessions { get; } = [];

    public Task<CookieSessionRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default)
        => Task.FromResult(Sessions.FirstOrDefault(s => s.Id == id));

    public Task<CookieSessionRecord?> GetByCookieIdAsync(string cookieId, CancellationToken ct = default)
        => Task.FromResult(Sessions.FirstOrDefault(s => s.CookieId == cookieId));

    public Task InsertAsync(CookieSessionRecord record, CancellationToken ct = default)
    {
        Sessions.Add(record);
        return Task.CompletedTask;
    }

    public Task<bool> TransitionStatusAsync(ObjectId id, string expectedStatus, string newStatus, CancellationToken ct = default)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == id);
        if (session is null || session.Status != expectedStatus) return Task.FromResult(false);
        session.Status = newStatus;
        return Task.FromResult(true);
    }

    public Task UpdateAsync(CookieSessionRecord record, CancellationToken ct = default)
    {
        var idx = Sessions.FindIndex(s => s.Id == record.Id);
        if (idx >= 0) Sessions[idx] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        Sessions.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CookieSessionRecord>> GetByStatusAsync(string status, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CookieSessionRecord>>(Sessions.Where(s => s.Status == status).ToList());

    public Task<IReadOnlyList<CookieSessionRecord>> GetPendingExpiredAsync(DateTime now, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CookieSessionRecord>>(
            Sessions.Where(s => s.Status == "pending_login" && s.ExpiresAt < now).ToList());

    public Task IncrementRefreshFailureAsync(ObjectId id, CancellationToken ct = default)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == id);
        if (session is not null) session.ConsecutiveRefreshFailures++;
        return Task.CompletedTask;
    }
}
