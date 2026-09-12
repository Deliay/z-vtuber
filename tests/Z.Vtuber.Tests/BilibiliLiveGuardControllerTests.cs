using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Controllers;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class BilibiliLiveGuardControllerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliLiveGuardController CreateController(IMongoDatabase database) => new(
        new LiveGuardRepository(database));

    private static LiveGuardRecord Record(long mid, string date, long roomId = 123456, params (long Uid, string Name, int GuardLevel, string? MedalName, int MedalLevel)[] guards) => new()
    {
        Id = MongoDB.Bson.ObjectId.GenerateNewId(),
        Mid = mid,
        RoomId = roomId,
        Date = date,
        Guards = guards.Select(g => new LiveGuardEntry
        {
            Uid = g.Uid,
            UserName = g.Name,
            GuardLevel = g.GuardLevel,
            MedalName = g.MedalName,
            MedalLevel = g.MedalLevel,
        }).ToList(),
        GuardCount = guards.Length,
        FetchedAt = DateTime.UtcNow,
    };

    private static JsonElement Ok(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static JsonElement BadRequest(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        return JsonSerializer.SerializeToElement(badRequest.Value);
    }

    [Fact]
    public async Task GetOnboardGuardUids_Normal_ReturnsPagedStructure()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, "medal1", 10)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, "medal1", 10)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (2, "b", 2, null, 0)));

        var json = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-31"));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);

        // 验证按 uid 升序
        Assert.Equal(1, items[0].GetProperty("uid").GetInt64());
        Assert.Equal(2, items[1].GetProperty("uid").GetInt64());

        // 验证字段完整
        Assert.Equal("a", items[0].GetProperty("userName").GetString());
        Assert.Equal(1, items[0].GetProperty("guardLevel").GetInt32());
        Assert.Equal("medal1", items[0].GetProperty("medalName").GetString());
        Assert.Equal(10, items[0].GetProperty("medalLevel").GetInt32());
    }

    [Fact]
    public async Task GetOnboardGuardUids_SingleDay_AnyAndAllAreSame()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));

        var jsonAny = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-30", mode: "any"));

        var jsonAll = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-30", mode: "all"));

        Assert.Equal(jsonAny.GetProperty("total").GetInt32(), jsonAll.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetOnboardGuardUids_DefaultMode_IsAny()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0)));

        var jsonDefault = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-31"));

        var jsonAny = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-31", mode: "any"));

        Assert.Equal(jsonDefault.GetProperty("total").GetInt32(), jsonAny.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetOnboardGuardUids_ModeAll_OnlyReturnsUidsWithAllDays()
    {
        var repository = new LiveGuardRepository(fixture.Database);
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-30", 123456, (1, "a", 1, null, 0), (2, "b", 2, null, 0)));
        await repository.UpsertDailySnapshotAsync(Record(1001, "2026-08-31", 123456, (1, "a", 1, null, 0)));

        var json = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-31", mode: "all"));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(1, json.GetProperty("items")[0].GetProperty("uid").GetInt64());
    }

    [Fact]
    public async Task GetOnboardGuardUids_MissingStartedAt_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: null, endedAt: "2026-08-31");

        var json = BadRequest(result);
        Assert.Contains("startedAt is required", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOnboardGuardUids_MissingEndedAt_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: null);

        var json = BadRequest(result);
        Assert.Contains("endedAt is required", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOnboardGuardUids_InvalidDateFormat_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30T00:00:00", endedAt: "2026-08-31");

        var json = BadRequest(result);
        Assert.Contains("yyyy-MM-dd", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOnboardGuardUids_StartedAfterEnded_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-31", endedAt: "2026-08-30");

        var json = BadRequest(result);
        Assert.Contains("startedAt must not be later than endedAt", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOnboardGuardUids_RangeOver366Days_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2025-08-30", endedAt: "2026-08-31");

        var json = BadRequest(result);
        Assert.Contains("366 days", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOnboardGuardUids_InvalidMode_ReturnsBadRequest()
    {
        var result = await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-31", mode: "invalid");

        var json = BadRequest(result);
        Assert.Contains("mode must be 'any' or 'all'", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOnboardGuardUids_NoSnapshots_ReturnsEmptyList()
    {
        var json = Ok(await CreateController(fixture.Database)
            .GetOnboardGuardUids(123456, startedAt: "2026-08-30", endedAt: "2026-08-31"));

        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
    }
}
