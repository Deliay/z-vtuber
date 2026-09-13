using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Notifier.Dispatch;
using Z.Vtuber.Notifier.Milky;
using Z.Vtuber.Tests;

namespace Z.Vtuber.Tests;

public class NotificationDispatchServiceTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DispatchAsync_NoSubscriptions_Ignores()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        var milky = new FakeMilkyApiClient();
        var service = new NotificationDispatchService(repo, milky, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Empty(milky.SendMessageCalls);
    }

    [Fact]
    public async Task DispatchAsync_WithSubscriptions_SendsToAll()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "789012",
            GroupName = "测试群",
        });

        var milky = new FakeMilkyApiClient();
        var service = new NotificationDispatchService(repo, milky, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Equal(2, milky.SendMessageCalls.Count);
        Assert.Contains(milky.SendMessageCalls, c => c.GroupId == 123456);
        Assert.Contains(milky.SendMessageCalls, c => c.GroupId == 789012);
    }

    [Fact]
    public async Task DispatchAsync_SingleSendFails_ContinuesOthers()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "789012",
            GroupName = "测试群",
        });

        var milky = new FakeMilkyApiClient();
        milky.SendMessageFunc = (groupId, text) =>
        {
            if (groupId == 123456)
                throw new InvalidOperationException("send failed");
            return Task.CompletedTask;
        };

        var service = new NotificationDispatchService(repo, milky, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Single(milky.SendMessageCalls);
        Assert.Contains(milky.SendMessageCalls, c => c.GroupId == 789012);
    }

    [Fact]
    public async Task DispatchAsync_MessageContainsCorrectInfo()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        });

        var milky = new FakeMilkyApiClient();
        var service = new NotificationDispatchService(repo, milky, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            Mid = 12345,
            Uname = "TestUser",
            OccurredAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Single(milky.SendMessageCalls);
        var message = milky.SendMessageCalls[0].Text;
        Assert.Contains("TestUser", message);
        Assert.Contains("12345", message);
        Assert.DoesNotContain("敏感", message);
    }

    [Fact]
    public async Task DispatchAsync_NoAccountInfo_ShowsShortCookieId()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        });

        var milky = new FakeMilkyApiClient();
        var service = new NotificationDispatchService(repo, milky, NullLogger<NotificationDispatchService>.Instance);

        var cookieId = ObjectId.GenerateNewId().ToString();
        var evt = new CookieInvalidEvent
        {
            CookieId = cookieId,
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Single(milky.SendMessageCalls);
        var message = milky.SendMessageCalls[0].Text;
        Assert.Contains($"CookieId {cookieId[..8]}...", message);
    }
}