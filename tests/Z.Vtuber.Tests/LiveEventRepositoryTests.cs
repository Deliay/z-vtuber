using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class LiveEventRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime BaseDate = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LiveEventRecord Event(long roomId, string type, DateTime occurredAt, long price = 0) => new()
    {
        RoomId = roomId,
        Type = type,
        OccurredAt = occurredAt,
        Payload = "{}",
        Price = price,
    };

    [Fact]
    public async Task GetPage_SingleType_FiltersByType()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(1)));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2)));
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(3)));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, ["danmaku"]);

        Assert.Equal(2, total);
        Assert.All(items, i => Assert.Equal("danmaku", i.Type));
    }

    [Fact]
    public async Task GetPage_MultipleTypes_ReturnsUnion()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(1)));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2)));
        await repository.InsertAsync(Event(1, "super_chat", BaseDate.AddHours(3)));
        await repository.InsertAsync(Event(1, "guard_buy", BaseDate.AddHours(4)));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, ["danmaku", "super_chat"]);

        Assert.Equal(2, total);
        Assert.Equal(["danmaku", "super_chat"], items.Select(i => i.Type).OrderBy(t => t).ToArray());
    }

    [Fact]
    public async Task GetPage_NoTypes_ReturnsAllTypes()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(1)));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2)));

        var (total, items) = await repository.GetPageAsync(1, 1, 100);

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetPage_UnknownType_ReturnsEmpty()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(1)));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, ["no_such_type"]);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task GetPage_TimeRange_IsInclusive()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 4; i++)
        {
            await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(i)));
        }

        var (total, items) = await repository.GetPageAsync(
            1, 1, 100, startedAt: BaseDate.AddHours(2), endedAt: BaseDate.AddHours(3));

        Assert.Equal(2, total);
        Assert.Equal(BaseDate.AddHours(2), items[0].OccurredAt);
        Assert.Equal(BaseDate.AddHours(3), items[1].OccurredAt);
    }

    [Fact]
    public async Task GetPage_SingleSidedBoundary_FiltersOneSide()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 3; i++)
        {
            await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(i)));
        }

        var (lowerTotal, lowerItems) = await repository.GetPageAsync(
            1, 1, 100, startedAt: BaseDate.AddHours(2));
        Assert.Equal(2, lowerTotal);
        Assert.All(lowerItems, i => Assert.True(i.OccurredAt >= BaseDate.AddHours(2)));

        var (upperTotal, upperItems) = await repository.GetPageAsync(
            1, 1, 100, endedAt: BaseDate.AddHours(2));
        Assert.Equal(2, upperTotal);
        Assert.All(upperItems, i => Assert.True(i.OccurredAt <= BaseDate.AddHours(2)));
    }

    [Fact]
    public async Task GetPage_NoTimeLimit_ReturnsAll()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 3; i++)
        {
            await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(i)));
        }

        var (total, items) = await repository.GetPageAsync(1, 1, 100);

        Assert.Equal(3, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetPage_PaginatesWithoutOverlapOrGap()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 5; i++)
        {
            await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(i)));
        }

        var (total, page1) = await repository.GetPageAsync(1, 1, 2);
        var (_, page2) = await repository.GetPageAsync(1, 2, 2);
        var (_, page3) = await repository.GetPageAsync(1, 3, 2);

        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Single(page3);

        var all = page1.Concat(page2).Concat(page3).Select(i => i.OccurredAt).ToArray();
        Assert.Equal(5, all.Distinct().Count());
        Assert.Equal(all.OrderBy(t => t).ToArray(), all);
    }

    [Fact]
    public async Task GetPage_TotalMatchesFilteredCount()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 5; i++)
        {
            await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(i)));
        }
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(6)));
        await repository.InsertAsync(Event(2, "danmaku", BaseDate.AddHours(1)));

        var (total, items) = await repository.GetPageAsync(1, 1, 2, ["danmaku"]);

        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetPage_EmptyRoom_ReturnsTotalZero()
    {
        var repository = new LiveEventRepository(fixture.Database);

        var (total, items) = await repository.GetPageAsync(999999, 1, 20);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task GetPage_MinPrice_FiltersGiftByPrice()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(1), price: 100));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2), price: 50));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(3), price: 200));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, minPriceGoldSeeds: 100);

        Assert.Equal(2, total);
        Assert.All(items, i => Assert.True(i.Price >= 100));
    }

    [Fact]
    public async Task GetPage_MinPrice_NonGiftType_NotFiltered()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(1), price: 0));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2), price: 100));
        await repository.InsertAsync(Event(1, "super_chat", BaseDate.AddHours(3), price: 0));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, minPriceGoldSeeds: 100);

        Assert.Equal(3, total);
        Assert.Contains(items, i => i.Type == "danmaku");
        Assert.Contains(items, i => i.Type == "gift");
        Assert.Contains(items, i => i.Type == "super_chat");
    }

    [Fact]
    public async Task GetPage_MinPrice_HistoricalRecords_PriceZero_Filtered()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(1), price: 0));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2), price: 100));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, minPriceGoldSeeds: 1);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal(100, items[0].Price);
    }

    [Fact]
    public async Task GetPage_MinPrice_WithTypeFilter_Combined()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(1), price: 100));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2), price: 50));
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(3), price: 0));
        await repository.InsertAsync(Event(1, "super_chat", BaseDate.AddHours(4), price: 0));

        var (total, items) = await repository.GetPageAsync(1, 1, 100, ["gift"], minPriceGoldSeeds: 100);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("gift", items[0].Type);
        Assert.Equal(100, items[0].Price);
    }

    [Fact]
    public async Task GetPage_MinPrice_WithTimeRange_Combined()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(1), price: 100));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2), price: 50));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(3), price: 200));

        var (total, items) = await repository.GetPageAsync(
            1, 1, 100, startedAt: BaseDate.AddHours(2), endedAt: BaseDate.AddHours(3), minPriceGoldSeeds: 100);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal(200, items[0].Price);
    }

    [Fact]
    public async Task GetPage_MinPrice_TotalMatchesFilteredCount()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 5; i++)
        {
            await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(i), price: i * 10));
        }
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(6), price: 0));

        var (total, items) = await repository.GetPageAsync(1, 1, 2, minPriceGoldSeeds: 30);

        Assert.Equal(4, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetPage_MinPrice_PaginateWithoutOverlapOrGap()
    {
        var repository = new LiveEventRepository(fixture.Database);
        for (var i = 1; i <= 5; i++)
        {
            await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(i), price: i * 10));
        }

        var (total, page1) = await repository.GetPageAsync(1, 1, 2, minPriceGoldSeeds: 10);
        var (_, page2) = await repository.GetPageAsync(1, 2, 2, minPriceGoldSeeds: 10);
        var (_, page3) = await repository.GetPageAsync(1, 3, 2, minPriceGoldSeeds: 10);

        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Single(page3);

        var all = page1.Concat(page2).Concat(page3).Select(i => i.OccurredAt).ToArray();
        Assert.Equal(5, all.Distinct().Count());
        Assert.Equal(all.OrderBy(t => t).ToArray(), all);
    }

    [Fact]
    public async Task GetPage_WithoutMinPrice_ReturnsAll()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(1), price: 100));
        await repository.InsertAsync(Event(1, "gift", BaseDate.AddHours(2), price: 50));
        await repository.InsertAsync(Event(1, "danmaku", BaseDate.AddHours(3), price: 0));

        var (total, items) = await repository.GetPageAsync(1, 1, 100);

        Assert.Equal(3, total);
        Assert.Equal(3, items.Count);
    }
}
