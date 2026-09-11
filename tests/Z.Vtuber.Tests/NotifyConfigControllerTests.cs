using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Notifier.Controllers;
using Z.Vtuber.Notifier.Satori;
using Z.Vtuber.Tests;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class NotifyConfigControllerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private NotifyConfigController CreateController(FakeSatoriApiClient? satori = null)
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        return new NotifyConfigController(repo, satori ?? new FakeSatoriApiClient(), NullLogger<NotifyConfigController>.Instance);
    }

    [Fact]
    public async Task GetSubscriptions_ReturnsEmptyList()
    {
        var controller = CreateController();

        var result = await controller.GetSubscriptions(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value?.GetType().GetProperty("items")?.GetValue(okResult.Value) as System.Collections.IList;
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetSubscriptions_ReturnsExistingBindings()
    {
        var controller = CreateController();
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });

        var result = await controller.GetSubscriptions(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value?.GetType().GetProperty("items")?.GetValue(okResult.Value) as System.Collections.IList;
        Assert.NotNull(items);
        Assert.Single(items!);
    }

    [Fact]
    public async Task CreateSubscription_InvalidEventType_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "invalid_type",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateSubscription_ValidRequest_CreatesRecord()
    {
        var controller = CreateController();

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            GuildName = "Test Guild",
            ChannelId = "channel1",
            ChannelName = "Test Channel",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result);
        var response = createdResult.Value as NotificationSubscriptionResponse;
        Assert.NotNull(response);
        Assert.Equal("cookie_invalid", response!.EventType);
        Assert.Equal("channel1", response.ChannelId);
    }

    [Fact]
    public async Task CreateSubscription_DuplicateIdempotent_ReturnsOk()
    {
        var controller = CreateController();

        await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        }, CancellationToken.None);

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        }, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult);
    }

    [Fact]
    public async Task DeleteSubscription_Existing_Deletes()
    {
        var controller = CreateController();
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        var inserted = await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });

        var result = await controller.DeleteSubscription(inserted.Id.ToString(), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteSubscription_NonExisting_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.DeleteSubscription(ObjectId.GenerateNewId().ToString(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteSubscription_InvalidId_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.DeleteSubscription("invalid-id", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetChannels_SatoriUnavailable_Returns503()
    {
        var satori = new FakeSatoriApiClient();
        satori.GuildListFunc = () => throw new InvalidOperationException("Satori unavailable");
        var controller = CreateController(satori);

        var result = await controller.GetChannels(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetChannels_WithGuilds_ReturnsChannels()
    {
        var satori = new FakeSatoriApiClient();
        satori.GuildListFunc = () => Task.FromResult<IReadOnlyList<Guild>>([
            new Guild { Id = "guild1", Name = "Guild 1" }
        ]);
        satori.ChannelListFunc = (guildId) => Task.FromResult<IReadOnlyList<Channel>>([
            new Channel { Id = "channel1", Name = "Channel 1" }
        ]);
        satori.CachedPlatform = "onebot";

        var controller = CreateController(satori);

        var result = await controller.GetChannels(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value?.GetType().GetProperty("items")?.GetValue(okResult.Value) as System.Collections.IList;
        Assert.NotNull(items);
        Assert.Single(items!);
    }
}
