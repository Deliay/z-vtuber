using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

public class WeiboSubscriptionControllerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static WeiboSubscriptionController CreateController(IMongoDatabase database) => new(
        new WeiboSubscriptionService(new WeiboSubscriptionRepository(database)));

    [Fact]
    public async Task Subscribe_ValidUid_ReturnsOkAndPersists()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Subscribe(new WeiboSubscribeRequest(12345), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<WeiboSubscriptionItemDto>(ok.Value);
        Assert.Equal(12345, dto.Uid);

        var service = new WeiboSubscriptionService(new WeiboSubscriptionRepository(fixture.Database));
        Assert.Equal([12345L], await service.GetUidsAsync());
    }

    [Fact]
    public async Task Subscribe_NullUid_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Subscribe(new WeiboSubscribeRequest(null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Subscribe_NonPositiveUid_ReturnsBadRequest(long uid)
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Subscribe(new WeiboSubscribeRequest(uid), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Subscribe_Duplicate_IsIdempotent()
    {
        var controller = CreateController(fixture.Database);

        var first = await controller.Subscribe(new WeiboSubscribeRequest(12345), CancellationToken.None);
        var second = await controller.Subscribe(new WeiboSubscribeRequest(12345), CancellationToken.None);

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<OkObjectResult>(second);
        var service = new WeiboSubscriptionService(new WeiboSubscriptionRepository(fixture.Database));
        Assert.Single(await service.GetUidsAsync());
    }

    [Fact]
    public async Task List_ReturnsSubscriptions()
    {
        var service = new WeiboSubscriptionService(new WeiboSubscriptionRepository(fixture.Database));
        await service.SubscribeAsync(12345);
        await service.SubscribeAsync(67890);

        var json = Ok(await CreateController(fixture.Database).List(CancellationToken.None));

        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Unsubscribe_Existing_ReturnsNoContent()
    {
        var service = new WeiboSubscriptionService(new WeiboSubscriptionRepository(fixture.Database));
        await service.SubscribeAsync(12345);
        var controller = CreateController(fixture.Database);

        var result = await controller.Unsubscribe(12345, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await service.GetUidsAsync());
    }

    [Fact]
    public async Task Unsubscribe_Missing_ReturnsNotFound()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Unsubscribe(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static JsonElement Ok(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
}
