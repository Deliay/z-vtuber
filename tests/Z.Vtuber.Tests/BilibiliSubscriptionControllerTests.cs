using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class BilibiliSubscriptionControllerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliSubscriptionController CreateController(IMongoDatabase database) => new(
        new SubscriptionService(database));

    [Fact]
    public async Task Subscribe_Guard_ReturnsOkAndPersists()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Subscribe(new SubscribeRequest(1150976664, "guard"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SubscriptionItemDto>(ok.Value);
        Assert.Equal(SubscriptionType.Guard, dto.Type);

        var service = new SubscriptionService(fixture.Database);
        Assert.Equal([1150976664L], await service.GetMidsAsync(SubscriptionType.Guard));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bogus")]
    [InlineData("123")]
    public async Task Subscribe_InvalidType_ReturnsBadRequest(string? type)
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Subscribe(new SubscribeRequest(1150976664, type), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Unsubscribe_Guard_RemovesGuardOnly()
    {
        var service = new SubscriptionService(fixture.Database);
        await service.SubscribeAsync(1150976664, SubscriptionType.Guard);
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);

        var controller = CreateController(fixture.Database);

        var removed = await controller.Unsubscribe(1150976664, "guard", CancellationToken.None);

        Assert.IsType<NoContentResult>(removed);
        Assert.Empty(await service.GetMidsAsync(SubscriptionType.Guard));
        Assert.Equal([1150976664L], await service.GetMidsAsync(SubscriptionType.Dynamic));
    }

    [Fact]
    public async Task Unsubscribe_Guard_Missing_ReturnsNotFound()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.Unsubscribe(1150976664, "guard", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}