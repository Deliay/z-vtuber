using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Controllers;
using Z.Vtuber.Live;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class BilibiliLiveControllerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliLiveController CreateController(IMongoDatabase database) => new(
        new LiveStatusRepository(database),
        new LiveEventRepository(database),
        new LiveSessionRepository(database),
        new LiveEventBus());

    private static LiveSessionRecord Session(string liveId, DateTime startDate, bool isFinish = true) => new()
    {
        LiveId = liveId,
        RoomId = 123456,
        Title = $"title-{liveId}",
        IsFinish = isFinish,
        StartDate = startDate,
        StopDate = isFinish ? startDate.AddHours(1) : null,
        FetchedAt = DateTime.UtcNow,
    };

    private static JsonElement Ok(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Fact]
    public async Task GetHistory_ReturnsStartDateDescending()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 3; i++)
        {
            await repository.UpsertManyAsync([Session($"live-{i}", baseDate.AddHours(i))]);
        }

        var json = Ok(await CreateController(fixture.Database).GetHistory(123456));

        Assert.Equal(3, json.GetProperty("total").GetInt32());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("live-3", items[0].GetProperty("liveId").GetString());
        Assert.Equal("live-2", items[1].GetProperty("liveId").GetString());
        Assert.Equal("live-1", items[2].GetProperty("liveId").GetString());
    }

    [Fact]
    public async Task GetHistory_ClampsPageAndPageSize()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 3; i++)
        {
            await repository.UpsertManyAsync([Session($"live-{i}", baseDate.AddHours(i))]);
        }

        var controller = CreateController(fixture.Database);

        var json = Ok(await controller.GetHistory(123456, 0, 0));
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(1, json.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, json.GetProperty("items").GetArrayLength());

        var bigJson = Ok(await controller.GetHistory(123456, -5, 500));
        Assert.Equal(1, bigJson.GetProperty("page").GetInt32());
        Assert.Equal(100, bigJson.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task GetHistory_EmptyRoom_ReturnsEmpty()
    {
        var json = Ok(await CreateController(fixture.Database).GetHistory(999999));

        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetHistory_OngoingSession_HasNullStopDate()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await repository.UpsertManyAsync([Session("live-ongoing", baseDate, isFinish: false)]);

        var json = Ok(await CreateController(fixture.Database).GetHistory(123456));

        var item = json.GetProperty("items")[0];
        Assert.False(item.GetProperty("isFinish").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("stopDate").ValueKind);
    }
}
