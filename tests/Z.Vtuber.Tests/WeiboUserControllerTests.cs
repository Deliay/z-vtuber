using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class WeiboUserControllerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const long Uid = 2147483647;

    private static WeiboUserController CreateController(IMongoDatabase database) =>
        new(new WeiboTimelineRepository(database));

    [Fact]
    public async Task GetTimeline_TimeRange_FiltersByPubTs()
    {
        var repository = new WeiboTimelineRepository(fixture.Database);
        await repository.BulkUpsertAsync([
            Record("1", 1000L),
            Record("2", 2000L),
            Record("3", 3000L),
        ]);

        var json = Ok(await CreateController(fixture.Database).GetTimeline(
            Uid,
            startedAt: DateTimeOffset.FromUnixTimeSeconds(2000).UtcDateTime,
            endedAt: DateTimeOffset.FromUnixTimeSeconds(3000).UtcDateTime));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetTimeline_StartedAfterEnded_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database).GetTimeline(
            Uid,
            startedAt: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetTimeline_PassesThroughRawMblogData()
    {
        var repository = new WeiboTimelineRepository(fixture.Database);
        await repository.BulkUpsertAsync([Record("m1", 1000L, raw: """{"id":"m1","text":"hello","user":{"id":"u1"}}""")]);

        var json = Ok(await CreateController(fixture.Database).GetTimeline(Uid));

        var item = json.GetProperty("items")[0];
        Assert.Equal("m1", item.GetProperty("mblogId").GetString());
        Assert.Equal("hello", item.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetTimeline_Empty_ReturnsEmptyItems()
    {
        var json = Ok(await CreateController(fixture.Database).GetTimeline(Uid));

        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    private static WeiboTimelineRecord Record(string mblogId, long? pubTs = null, string? raw = null) => new()
    {
        Uid = Uid,
        MblogId = mblogId,
        Data = raw ?? $@"{{""id"":""{mblogId}""}}",
        PubTs = pubTs,
        FetchedAt = DateTime.UtcNow,
    };

    private static JsonElement Ok(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
}
