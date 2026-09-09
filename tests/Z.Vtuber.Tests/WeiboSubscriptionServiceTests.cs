using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class WeiboSubscriptionServiceTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static WeiboSubscriptionService CreateService(IMongoDatabase database) =>
        new(new WeiboSubscriptionRepository(database));

    [Fact]
    public async Task Subscribe_RaisesEvent_OnNewSubscription()
    {
        var service = CreateService(fixture.Database);
        var subscribed = new List<long>();
        service.Subscribed += uid => subscribed.Add(uid);

        await service.SubscribeAsync(12345);

        Assert.Equal([12345L], subscribed);
    }

    [Fact]
    public async Task Subscribe_IsIdempotent_DoesNotRaiseEventAgain()
    {
        var service = CreateService(fixture.Database);
        var subscribed = new List<long>();
        service.Subscribed += uid => subscribed.Add(uid);

        await service.SubscribeAsync(12345);
        await service.SubscribeAsync(12345);

        Assert.Single(subscribed);
        Assert.Single(await service.GetUidsAsync());
    }

    [Fact]
    public async Task Subscribe_InvalidUid_Throws()
    {
        var service = CreateService(fixture.Database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SubscribeAsync(0));
    }

    [Fact]
    public async Task Unsubscribe_RemovesSubscription_ReturnsTrue()
    {
        var service = CreateService(fixture.Database);
        await service.SubscribeAsync(12345);

        var removed = await service.UnsubscribeAsync(12345);

        Assert.True(removed);
        Assert.Empty(await service.GetUidsAsync());
    }

    [Fact]
    public async Task Unsubscribe_Missing_ReturnsFalse()
    {
        var service = CreateService(fixture.Database);

        var removed = await service.UnsubscribeAsync(999);

        Assert.False(removed);
    }

    [Fact]
    public async Task GetAll_ReturnsSubscriptionsWithSubscribedAt()
    {
        var service = CreateService(fixture.Database);
        await service.SubscribeAsync(1);
        await service.SubscribeAsync(2);

        var all = await service.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, s => Assert.True(s.SubscribedAt > default(DateTime)));
    }
}
