using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class SubscriptionServiceTests(MongoDbFixture fixture) : IAsyncLifetime
{
    private SubscriptionService CreateService() => new(fixture.Database);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Subscribe_IsIdempotent()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664);
        await service.SubscribeAsync(1150976664);

        var mids = await service.GetMidsAsync();
        Assert.Equal([1150976664L], mids);
    }

    [Fact]
    public async Task GetAll_ReturnsSubscribedAt()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664);

        var all = await service.GetAllAsync();

        var single = Assert.Single(all);
        Assert.Equal(1150976664L, single.Mid);
        Assert.True(single.SubscribedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task Unsubscribe_RemovesOnlyTarget()
    {
        var service = CreateService();
        await service.SubscribeAsync(1);
        await service.SubscribeAsync(2);

        var removed = await service.UnsubscribeAsync(1);

        Assert.True(removed);
        Assert.Equal([2L], await service.GetMidsAsync());
    }

    [Fact]
    public async Task Unsubscribe_Missing_ReturnsFalse()
    {
        var service = CreateService();

        Assert.False(await service.UnsubscribeAsync(999));
    }

    [Fact]
    public async Task Subscribe_NonPositiveMid_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SubscribeAsync(0));
    }
}
