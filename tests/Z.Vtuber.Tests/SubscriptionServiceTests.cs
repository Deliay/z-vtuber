using System.Text.Json;
using System.Text.Json.Serialization;
using Z.Vtuber.Persistence.Entities;
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
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);

        var mids = await service.GetMidsAsync(SubscriptionType.Dynamic);
        Assert.Equal([1150976664L], mids);
    }

    [Fact]
    public async Task GetAll_ReturnsSubscribedAt()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);

        var all = await service.GetAllAsync();

        var single = Assert.Single(all);
        Assert.Equal(1150976664L, single.Mid);
        Assert.Equal(SubscriptionType.Dynamic, single.Type);
        Assert.True(single.SubscribedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task Unsubscribe_RemovesOnlyTarget()
    {
        var service = CreateService();
        await service.SubscribeAsync(1, SubscriptionType.Dynamic);
        await service.SubscribeAsync(2, SubscriptionType.Dynamic);

        var removed = await service.UnsubscribeAsync(1, SubscriptionType.Dynamic);

        Assert.True(removed);
        Assert.Equal([2L], await service.GetMidsAsync(SubscriptionType.Dynamic));
    }

    [Fact]
    public async Task Unsubscribe_Missing_ReturnsFalse()
    {
        var service = CreateService();

        Assert.False(await service.UnsubscribeAsync(999, SubscriptionType.Dynamic));
    }

    [Fact]
    public async Task Subscribe_NonPositiveMid_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SubscribeAsync(0, SubscriptionType.Dynamic));
    }

    [Fact]
    public async Task Subscribe_SameMid_DifferentTypes_CreatedSeparately()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);
        await service.SubscribeAsync(1150976664, SubscriptionType.Danmaku);

        var dynamicMids = await service.GetMidsAsync(SubscriptionType.Dynamic);
        var danmakuMids = await service.GetMidsAsync(SubscriptionType.Danmaku);

        Assert.Equal([1150976664L], dynamicMids);
        Assert.Equal([1150976664L], danmakuMids);

        var all = await service.GetAllAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Unsubscribe_OneType_DoesNotAffectOther()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);
        await service.SubscribeAsync(1150976664, SubscriptionType.Danmaku);

        await service.UnsubscribeAsync(1150976664, SubscriptionType.Dynamic);

        var dynamicMids = await service.GetMidsAsync(SubscriptionType.Dynamic);
        var danmakuMids = await service.GetMidsAsync(SubscriptionType.Danmaku);

        Assert.Empty(dynamicMids);
        Assert.Equal([1150976664L], danmakuMids);
    }

    [Fact]
    public void SubscriptionType_SerializesToCamelCase()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var dynamicJson = JsonSerializer.Serialize(SubscriptionType.Dynamic, options);
        var danmakuJson = JsonSerializer.Serialize(SubscriptionType.Danmaku, options);

        // JsonSerializer.Serialize wraps strings in quotes
        Assert.Equal("\"dynamic\"", dynamicJson);
        Assert.Equal("\"danmaku\"", danmakuJson);

        // Verify the actual content matches contract
        Assert.Equal("dynamic", dynamicJson.Trim('"'));
        Assert.Equal("danmaku", danmakuJson.Trim('"'));
    }

    [Fact]
    public async Task Subscribe_Guard_IsIdempotent()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664, SubscriptionType.Guard);
        await service.SubscribeAsync(1150976664, SubscriptionType.Guard);

        var mids = await service.GetMidsAsync(SubscriptionType.Guard);
        Assert.Equal([1150976664L], mids);
    }

    [Fact]
    public async Task GetMids_Guard_StrictlyFilters()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664, SubscriptionType.Guard);
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);
        await service.SubscribeAsync(222, SubscriptionType.Guard);

        Assert.Equal([1150976664L, 222L], await service.GetMidsAsync(SubscriptionType.Guard));
        Assert.Equal([1150976664L], await service.GetMidsAsync(SubscriptionType.Dynamic));
    }

    [Fact]
    public async Task Unsubscribe_Guard_DoesNotAffectOtherTypesOnSameMid()
    {
        var service = CreateService();
        await service.SubscribeAsync(1150976664, SubscriptionType.Guard);
        await service.SubscribeAsync(1150976664, SubscriptionType.Dynamic);

        var removed = await service.UnsubscribeAsync(1150976664, SubscriptionType.Guard);

        Assert.True(removed);
        Assert.Empty(await service.GetMidsAsync(SubscriptionType.Guard));
        Assert.Equal([1150976664L], await service.GetMidsAsync(SubscriptionType.Dynamic));
    }

    [Fact]
    public void SubscriptionType_Guard_SerializesToCamelCase()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var guardJson = JsonSerializer.Serialize(SubscriptionType.Guard, options);

        Assert.Equal("\"guard\"", guardJson);
        Assert.Equal("guard", guardJson.Trim('"'));
    }
}
