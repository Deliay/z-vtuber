using MongoDB.Bson;
using Z.Vtuber.Cookie.Persistence;

namespace Z.Vtuber.Tests;

public class CookieSessionRepositoryTests(MongoDbFixture db) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private CookieSessionRepository CreateRepository() => new(db.Database);

    [Fact]
    public async Task InsertAndGetById()
    {
        var repo = CreateRepository();
        var record = new CookieSessionRecord
        {
            Status = "pending_login",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };

        await repo.InsertAsync(record);

        var found = await repo.GetByIdAsync(record.Id);
        Assert.NotNull(found);
        Assert.Equal(record.Id, found.Id);
        Assert.Equal("pending_login", found.Status);
    }

    [Fact]
    public async Task GetByCookieId()
    {
        var repo = CreateRepository();
        var record = new CookieSessionRecord
        {
            Status = "active",
            CookieId = "test-cookie-id",
            Cookie = "SESSDATA=fake",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };

        await repo.InsertAsync(record);

        var found = await repo.GetByCookieIdAsync("test-cookie-id");
        Assert.NotNull(found);
        Assert.Equal(record.Id, found.Id);
    }

    [Fact]
    public async Task TransitionStatus_Success()
    {
        var repo = CreateRepository();
        var record = new CookieSessionRecord
        {
            Status = "pending_login",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };
        await repo.InsertAsync(record);

        var result = await repo.TransitionStatusAsync(record.Id, "pending_login", "active");
        Assert.True(result);

        var found = await repo.GetByIdAsync(record.Id);
        Assert.Equal("active", found!.Status);
    }

    [Fact]
    public async Task TransitionStatus_WrongExpectedStatus_ReturnsFalse()
    {
        var repo = CreateRepository();
        var record = new CookieSessionRecord
        {
            Status = "pending_login",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };
        await repo.InsertAsync(record);

        var result = await repo.TransitionStatusAsync(record.Id, "active", "invalid");
        Assert.False(result);

        var found = await repo.GetByIdAsync(record.Id);
        Assert.Equal("pending_login", found!.Status);
    }

    [Fact]
    public async Task GetByStatus()
    {
        var repo = CreateRepository();
        await repo.InsertAsync(new CookieSessionRecord { Status = "active", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
        await repo.InsertAsync(new CookieSessionRecord { Status = "active", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
        await repo.InsertAsync(new CookieSessionRecord { Status = "expired", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5) });

        var active = await repo.GetByStatusAsync("active");
        Assert.Equal(2, active.Count);

        var expired = await repo.GetByStatusAsync("expired");
        Assert.Single(expired);
    }

    [Fact]
    public async Task Delete()
    {
        var repo = CreateRepository();
        var record = new CookieSessionRecord
        {
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };
        await repo.InsertAsync(record);

        await repo.DeleteAsync(record.Id);

        var found = await repo.GetByIdAsync(record.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task IncrementRefreshFailure()
    {
        var repo = CreateRepository();
        var record = new CookieSessionRecord
        {
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ConsecutiveRefreshFailures = 0,
        };
        await repo.InsertAsync(record);

        await repo.IncrementRefreshFailureAsync(record.Id);
        var found = await repo.GetByIdAsync(record.Id);
        Assert.Equal(1, found!.ConsecutiveRefreshFailures);

        await repo.IncrementRefreshFailureAsync(record.Id);
        found = await repo.GetByIdAsync(record.Id);
        Assert.Equal(2, found!.ConsecutiveRefreshFailures);
    }

    [Fact]
    public async Task GetPendingExpired_ReturnsExpiredPendingSessions()
    {
        var repo = CreateRepository();
        await repo.InsertAsync(new CookieSessionRecord
        {
            Status = "pending_login",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await repo.InsertAsync(new CookieSessionRecord
        {
            Status = "pending_login",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });

        var expired = await repo.GetPendingExpiredAsync(DateTime.UtcNow);
        Assert.Single(expired);
    }
}
