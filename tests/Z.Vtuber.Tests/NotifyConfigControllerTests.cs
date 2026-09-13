using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Notifier.Controllers;
using Z.Vtuber.Notifier.Milky;
using Z.Vtuber.Tests;

namespace Z.Vtuber.Tests;

public class NotifyConfigControllerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private NotifyConfigController CreateController(FakeMilkyApiClient? milky = null)
    {
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        return new NotifyConfigController(repo, milky ?? new FakeMilkyApiClient(), NullLogger<NotifyConfigController>.Instance);
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
            EventType = "bilibili_cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
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
            GroupId = "123456",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateSubscription_InvalidGroupId_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "bilibili_cookie_invalid",
            GroupId = "not-a-number",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateSubscription_ValidRequest_CreatesRecord()
    {
        var controller = CreateController();

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "bilibili_cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result);
        var response = createdResult.Value as NotificationSubscriptionResponse;
        Assert.NotNull(response);
        Assert.Equal("bilibili_cookie_invalid", response!.EventType);
        Assert.Equal("123456", response.GroupId);
        Assert.Equal("运维群", response.GroupName);
    }

    [Fact]
    public async Task CreateSubscription_DuplicateIdempotent_ReturnsOk()
    {
        var controller = CreateController();

        await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "bilibili_cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        }, CancellationToken.None);

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "bilibili_cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        }, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult);
    }

    [Fact]
    public async Task CreateSubscription_WeiboEventType_CreatesRecord()
    {
        var controller = CreateController();

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "weibo_cookie_invalid",
            GroupId = "123456",
            GroupName = "微博群",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result);
        var response = createdResult.Value as NotificationSubscriptionResponse;
        Assert.NotNull(response);
        Assert.Equal("weibo_cookie_invalid", response!.EventType);
    }

    [Fact]
    public async Task CreateSubscription_LegacyCookieInvalid_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.CreateSubscription(new CreateNotificationSubscriptionRequest
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteSubscription_Existing_Deletes()
    {
        var controller = CreateController();
        var repo = new NotificationSubscriptionRepository(fixture.Database);
        var inserted = await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "bilibili_cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
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
    public async Task GetGroups_MilkyUnavailable_Returns503()
    {
        var milky = new FakeMilkyApiClient();
        milky.LoginInfoFunc = () => throw new InvalidOperationException("Milky unavailable");
        var controller = CreateController(milky);

        var result = await controller.GetGroups(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetGroups_MilkyNotLoggedIn_Returns503()
    {
        var milky = new FakeMilkyApiClient();
        milky.LoginInfoFunc = () => throw new InvalidOperationException("Bot not logged in");
        var controller = CreateController(milky);

        var result = await controller.GetGroups(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetGroups_WithGroups_ReturnsGroups()
    {
        var milky = new FakeMilkyApiClient();
        milky.GroupListFunc = () => Task.FromResult<IReadOnlyList<GroupInfo>>([
            new GroupInfo { GroupId = 123456, GroupName = "运维群" }
        ]);
        var controller = CreateController(milky);

        var result = await controller.GetGroups(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value?.GetType().GetProperty("items")?.GetValue(okResult.Value) as System.Collections.IList;
        Assert.NotNull(items);
        Assert.Single(items!);
    }

    [Fact]
    public async Task GetGroups_EmptyList_ReturnsEmptyList()
    {
        var milky = new FakeMilkyApiClient();
        milky.GroupListFunc = () => Task.FromResult<IReadOnlyList<GroupInfo>>([]);
        var controller = CreateController(milky);

        var result = await controller.GetGroups(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value?.GetType().GetProperty("items")?.GetValue(okResult.Value) as System.Collections.IList;
        Assert.NotNull(items);
        Assert.Empty(items!);
    }
}