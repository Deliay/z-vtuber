using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Common.Locking;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Persistence;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Tests;

public class SessionRefreshJobTests
{
    private readonly FakeDistributedLockProvider _lockProvider = new();
    private readonly FakeBrowserSessionManager _browserSessionManager = new();
    private readonly FakeCookiePoolService _cookiePool = new();

    private SessionRefreshJob CreateJob(
        ICookieSessionRepository? sessionRepository = null,
        ICookieInvalidEventPublisher? eventPublisher = null,
        CookieServiceOptions? options = null)
    {
        return new SessionRefreshJob(
            sessionRepository ?? new FakeCookieSessionRepository(),
            _browserSessionManager,
            _cookiePool,
            _lockProvider,
            eventPublisher ?? new FakeCookieInvalidEventPublisher(),
            Options.Create(options ?? new CookieServiceOptions
            {
                RefreshIntervalMinutes = 10,
                MaxConsecutiveRefreshFailures = 3,
                RefreshLockTtlSeconds = 90,
                LockAcquireWaitMs = 1000,
            }),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionRefreshJob>());
    }

    [Fact]
    public async Task RefreshActiveSessions_RefreshesSessionsPastThreshold()
    {
        var repo = new FakeCookieSessionRepository();
        var session = new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            Status = "active",
            Cookie = "old-cookie",
            LastRefreshAt = DateTime.UtcNow.AddMinutes(-15),
            ConsecutiveRefreshFailures = 0,
        };
        repo.Sessions.Add(session);

        var job = CreateJob(sessionRepository: repo);

        var method = typeof(SessionRefreshJob).GetMethod("RefreshActiveSessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(job, [CancellationToken.None])!;

        var updated = await repo.GetByIdAsync(session.Id);
        Assert.NotNull(updated);
        Assert.Equal(0, updated.ConsecutiveRefreshFailures);
        Assert.True(updated.LastRefreshAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Contains(session.Id.ToString(), _browserSessionManager.RefreshedSessions);
    }

    [Fact]
    public async Task RefreshActiveSessions_SkipsSessionsWithinThreshold()
    {
        var repo = new FakeCookieSessionRepository();
        var session = new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            Status = "active",
            Cookie = "current-cookie",
            LastRefreshAt = DateTime.UtcNow.AddMinutes(-5),
            ConsecutiveRefreshFailures = 0,
        };
        repo.Sessions.Add(session);

        var job = CreateJob(sessionRepository: repo);

        var method = typeof(SessionRefreshJob).GetMethod("RefreshActiveSessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(job, [CancellationToken.None])!;

        Assert.Empty(_browserSessionManager.RefreshedSessions);
    }

    [Fact]
    public async Task RefreshSession_FailureIncrementsCount()
    {
        var repo = new FakeCookieSessionRepository();
        var session = new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            Status = "active",
            Cookie = "old-cookie",
            LastRefreshAt = DateTime.UtcNow.AddMinutes(-15),
            ConsecutiveRefreshFailures = 0,
        };
        repo.Sessions.Add(session);

        _browserSessionManager.RefreshSessionAsyncFunc = (id) => throw new Exception("refresh failed");

        var job = CreateJob(sessionRepository: repo);

        var method = typeof(SessionRefreshJob).GetMethod("RefreshSessionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(job, [session, CancellationToken.None])!;

        var updated = await repo.GetByIdAsync(session.Id);
        Assert.Equal(1, updated!.ConsecutiveRefreshFailures);
    }

    [Fact]
    public async Task RefreshSession_ConsecutiveFailuresExceeded_InvalidatesSession()
    {
        var repo = new FakeCookieSessionRepository();
        var eventPublisher = new FakeCookieInvalidEventPublisher();
        var session = new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            Status = "active",
            Cookie = "old-cookie",
            CookieId = "cookie-id-123",
            LastRefreshAt = DateTime.UtcNow.AddMinutes(-15),
            ConsecutiveRefreshFailures = 2,
        };
        repo.Sessions.Add(session);

        _browserSessionManager.RefreshSessionAsyncFunc = (id) => throw new Exception("refresh failed");

        var job = CreateJob(sessionRepository: repo, eventPublisher: eventPublisher,
            options: new CookieServiceOptions { MaxConsecutiveRefreshFailures = 3 });

        var method = typeof(SessionRefreshJob).GetMethod("RefreshSessionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(job, [session, CancellationToken.None])!;

        var updated = await repo.GetByIdAsync(session.Id);
        Assert.Equal("invalid", updated!.Status);
        Assert.Contains(session.Id.ToString(), _browserSessionManager.DeletedSessions);
        Assert.Single(eventPublisher.PublishedEvents);
        Assert.Equal("refresh_nav_missing", eventPublisher.PublishedEvents[0].Reason);
    }

    [Fact]
    public async Task RefreshSession_SingleSessionException_DoesNotAffectOthers()
    {
        var repo = new FakeCookieSessionRepository();
        var session1 = new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            Status = "active",
            Cookie = "cookie1",
            LastRefreshAt = DateTime.UtcNow.AddMinutes(-15),
            ConsecutiveRefreshFailures = 0,
        };
        var session2 = new CookieSessionRecord
        {
            Id = ObjectId.GenerateNewId(),
            Status = "active",
            Cookie = "cookie2",
            LastRefreshAt = DateTime.UtcNow.AddMinutes(-15),
            ConsecutiveRefreshFailures = 0,
        };
        repo.Sessions.Add(session1);
        repo.Sessions.Add(session2);

        var callCount = 0;
        _browserSessionManager.RefreshSessionAsyncFunc = (id) =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count == 1) throw new Exception("first refresh failed");
            return Task.CompletedTask;
        };

        var job = CreateJob(sessionRepository: repo);

        var method = typeof(SessionRefreshJob).GetMethod("RefreshActiveSessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(job, [CancellationToken.None])!;

        var updated1 = await repo.GetByIdAsync(session1.Id);
        var updated2 = await repo.GetByIdAsync(session2.Id);
        Assert.Equal(1, updated1!.ConsecutiveRefreshFailures);
        Assert.Equal(0, updated2!.ConsecutiveRefreshFailures);
    }
}

public class FakeCookieInvalidEventPublisher : ICookieInvalidEventPublisher
{
    public List<(string CookieId, string Reason)> PublishedEvents { get; } = [];

    public Task PublishAsync(string cookieId, string reason, CancellationToken ct = default)
    {
        PublishedEvents.Add((cookieId, reason));
        return Task.CompletedTask;
    }
}
