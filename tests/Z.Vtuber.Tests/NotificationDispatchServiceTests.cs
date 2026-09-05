using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Notifier.Dispatch;
using Z.Vtuber.Notifier.Satori;
using Z.Vtuber.Tests;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class NotificationDispatchServiceTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DispatchAsync_NoSubscriptions_Ignores()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        var satori = new FakeSatoriApiClient();
        var service = new NotificationDispatchService(repo, satori, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Null(satori.CachedPlatform);
    }

    [Fact]
    public async Task DispatchAsync_WithSubscriptions_SendsToAll()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel2",
        });

        var sentChannels = new List<string>();
        var satori = new FakeSatoriApiClient();
        satori.SendMessageFunc = (channelId, content) =>
        {
            sentChannels.Add(channelId);
            return Task.CompletedTask;
        };

        var service = new NotificationDispatchService(repo, satori, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Equal(2, sentChannels.Count);
        Assert.Contains("channel1", sentChannels);
        Assert.Contains("channel2", sentChannels);
    }

    [Fact]
    public async Task DispatchAsync_SingleSendFails_ContinuesOthers()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel2",
        });

        var sentChannels = new List<string>();
        var satori = new FakeSatoriApiClient();
        satori.SendMessageFunc = (channelId, content) =>
        {
            if (channelId == "channel1")
                throw new InvalidOperationException("send failed");
            sentChannels.Add(channelId);
            return Task.CompletedTask;
        };

        var service = new NotificationDispatchService(repo, satori, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.Single(sentChannels);
        Assert.Contains("channel2", sentChannels);
    }

    [Fact]
    public async Task DispatchAsync_MessageContainsCorrectInfo()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });

        string? capturedContent = null;
        var satori = new FakeSatoriApiClient();
        satori.SendMessageFunc = (channelId, content) =>
        {
            capturedContent = content;
            return Task.CompletedTask;
        };

        var service = new NotificationDispatchService(repo, satori, NullLogger<NotificationDispatchService>.Instance);

        var evt = new CookieInvalidEvent
        {
            CookieId = ObjectId.GenerateNewId().ToString(),
            Mid = 12345,
            Uname = "TestUser",
            OccurredAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.NotNull(capturedContent);
        Assert.Contains("TestUser", capturedContent!);
        Assert.Contains("12345", capturedContent!);
        Assert.DoesNotContain("敏感", capturedContent!);
    }

    [Fact]
    public async Task DispatchAsync_NoAccountInfo_ShowsShortCookieId()
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });

        string? capturedContent = null;
        var satori = new FakeSatoriApiClient();
        satori.SendMessageFunc = (channelId, content) =>
        {
            capturedContent = content;
            return Task.CompletedTask;
        };

        var service = new NotificationDispatchService(repo, satori, NullLogger<NotificationDispatchService>.Instance);

        var cookieId = ObjectId.GenerateNewId().ToString();
        var evt = new CookieInvalidEvent
        {
            CookieId = cookieId,
            OccurredAt = DateTime.UtcNow,
            Reason = "test",
        };

        await service.DispatchAsync(evt);

        Assert.NotNull(capturedContent);
        Assert.Contains($"CookieId {cookieId[..8]}...", capturedContent!);
    }
}
