using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Controllers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class BilibiliUserControllerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliUserController CreateController(IMongoDatabase database) => new(
        new AccountBasicInfoRepository(database),
        new SpaceTimelineRepository(database));

    [Fact]
    public async Task GetSpaceTimeline_TimeRange_FiltersByPubTs()
    {
        var repository = new SpaceTimelineRepository(fixture.Database);
        await repository.BulkUpsertAsync([
            Record("1", pubTs: 1000),
            Record("2", pubTs: 2000),
            Record("3", pubTs: 3000),
        ]);

        var json = Ok(await CreateController(fixture.Database).GetSpaceTimeline(
            1150976664,
            startedAt: DateTimeOffset.FromUnixTimeSeconds(2000).UtcDateTime,
            endedAt: DateTimeOffset.FromUnixTimeSeconds(3000).UtcDateTime));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetSpaceTimeline_StartedAfterEnded_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database).GetSpaceTimeline(
            1150976664,
            startedAt: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetSpaceTimeline_NoTimeRange_ReturnsAll()
    {
        var repository = new SpaceTimelineRepository(fixture.Database);
        await repository.BulkUpsertAsync([Record("1", pubTs: 1000), Record("2", pubTs: 2000)]);

        var json = Ok(await CreateController(fixture.Database).GetSpaceTimeline(1150976664));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
    }

    private static JsonElement Ok(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static SpaceTimelineRecord Record(string dynamicId, long? pubTs = null) => new()
    {
        Mid = 1150976664,
        DynamicId = dynamicId,
        Data = $@"{{""id_str"":""{dynamicId}""}}",
        PubTs = pubTs,
        FetchedAt = DateTime.UtcNow,
    };
}
