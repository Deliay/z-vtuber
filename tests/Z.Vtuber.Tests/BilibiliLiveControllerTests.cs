using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Controllers;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

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
        new LiveEventTextIndexRepository(database),
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

    [Fact]
    public async Task GetHistory_TimeRange_FiltersSessions()
    {
        var repository = new LiveSessionRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 3; i++)
        {
            await repository.UpsertManyAsync([Session($"live-{i}", baseDate.AddHours(i))]);
        }

        var json = Ok(await CreateController(fixture.Database)
            .GetHistory(123456, startedAt: baseDate.AddHours(2), endedAt: baseDate.AddHours(3)));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetHistory_StartedAfterEnded_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.GetHistory(123456,
            startedAt: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetAllStates_TimeRange_FiltersStates()
    {
        var repository = new LiveStatusRepository(fixture.Database);
        var baseDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 3; i++)
        {
            await repository.InsertAsync(new LiveStatusRecord
            {
                RoomId = 123456,
                Title = "title",
                LiveStatus = true,
                CreatedAt = baseDate.AddHours(i),
            });
        }

        var json = Ok(await CreateController(fixture.Database)
            .GetAllStates(123456, startedAt: baseDate.AddHours(2), endedAt: baseDate.AddHours(3)));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetAllStates_StartedAfterEnded_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.GetAllStates(123456,
            startedAt: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static readonly DateTime EventBaseDate = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LiveEventRecord Event(string type, DateTime occurredAt, long price = 0) => new()
    {
        RoomId = 123456,
        Type = type,
        OccurredAt = occurredAt,
        Payload = "{}",
        Price = price,
    };

    private static async Task SeedEvents(LiveEventRepository repository, params LiveEventRecord[] records)
    {
        foreach (var record in records)
        {
            await repository.InsertAsync(record);
        }
    }

    [Fact]
    public async Task GetEventsRange_ReturnsPagedStructure()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("gift", EventBaseDate.AddHours(2)),
            Event("danmaku", EventBaseDate.AddHours(3)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, page: 1, pageSize: 2));

        Assert.Equal(3, json.GetProperty("total").GetInt32());
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(2, json.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetEventsRange_ClampsPageAndPageSize()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository, Event("danmaku", EventBaseDate.AddHours(1)));

        var controller = CreateController(fixture.Database);

        var json = Ok(await controller.GetEventsRange(123456, page: 0, pageSize: 0));
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(1, json.GetProperty("pageSize").GetInt32());

        var bigJson = Ok(await controller.GetEventsRange(123456, page: -5, pageSize: 500));
        Assert.Equal(1, bigJson.GetProperty("page").GetInt32());
        Assert.Equal(100, bigJson.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_BeyondLastPage_ReturnsEmptyWithRealTotal()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository, Event("danmaku", EventBaseDate.AddHours(1)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, page: 5, pageSize: 20));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetEventsRange_SingleType_Filters()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("gift", EventBaseDate.AddHours(2)),
            Event("danmaku", EventBaseDate.AddHours(3)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, type: ["danmaku"]));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.All(json.GetProperty("items").EnumerateArray(), i => Assert.Equal("danmaku", i.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task GetEventsRange_MultipleTypes_ReturnsUnion()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("gift", EventBaseDate.AddHours(2)),
            Event("super_chat", EventBaseDate.AddHours(3)),
            Event("guard_buy", EventBaseDate.AddHours(4)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, type: ["danmaku", "super_chat"]));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        var types = json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()!).ToHashSet();
        Assert.Equal(["danmaku", "super_chat"], types.OrderBy(t => t).ToArray());
    }

    [Fact]
    public async Task GetEventsRange_DuplicateTypes_AreDeduplicated()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("gift", EventBaseDate.AddHours(2)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, type: ["danmaku", "danmaku"]));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_BlankTypes_AreIgnored()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("gift", EventBaseDate.AddHours(2)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, type: ["danmaku", "", "  ", null!]));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal("danmaku", json.GetProperty("items")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetEventsRange_SingleSidedTimeBoundary_Filters()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("danmaku", EventBaseDate.AddHours(2)),
            Event("danmaku", EventBaseDate.AddHours(3)));

        var lowerJson = Ok(await CreateController(fixture.Database).GetEventsRange(123456, startedAt: EventBaseDate.AddHours(2)));
        Assert.Equal(2, lowerJson.GetProperty("total").GetInt32());

        var upperJson = Ok(await CreateController(fixture.Database).GetEventsRange(123456, endedAt: EventBaseDate.AddHours(2)));
        Assert.Equal(2, upperJson.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_NoTimeRange_ReturnsAll()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("danmaku", EventBaseDate.AddHours(1)),
            Event("gift", EventBaseDate.AddHours(2)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_StartedAfterEnded_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.GetEventsRange(123456,
            startedAt: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            endedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Subscribe_WithTypeFilter_OnlyPushesMatchingEvents()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        var content = await RunSubscribeAsync(controller, bus, 123456, ["danmaku"],
        [
            new LiveEvent(123456, "danmaku", null, EventBaseDate.AddHours(1), "{}"),
            new LiveEvent(123456, "gift", null, EventBaseDate.AddHours(2), "{}"),
            new LiveEvent(123456, "danmaku", null, EventBaseDate.AddHours(3), "{}"),
        ]);

        Assert.Equal(2, CountOccurrences(content, "event: danmaku\n"));
        Assert.DoesNotContain("event: gift", content);
    }

    [Fact]
    public async Task Subscribe_WithoutType_PushesAllEvents()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        var content = await RunSubscribeAsync(controller, bus, 123456, null,
        [
            new LiveEvent(123456, "danmaku", null, EventBaseDate.AddHours(1), "{}"),
            new LiveEvent(123456, "gift", null, EventBaseDate.AddHours(2), "{}"),
        ]);

        Assert.Contains("event: danmaku\n", content);
        Assert.Contains("event: gift\n", content);
        Assert.Contains("data: ", content);
    }

    private static async Task<string> RunSubscribeAsync(
        BilibiliLiveController controller, LiveEventBus bus, long roomId, string[]? type, IEnumerable<LiveEvent> events,
        TimeSpan? waitDuration = null, bool publishEvents = true)
    {
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = body } };
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        using var cts = new CancellationTokenSource();
        var subscribeTask = controller.Subscribe(roomId, type, cts.Token);

        if (publishEvents)
        {
            foreach (var evt in events)
            {
                bus.TryPublish(roomId, evt);
            }
        }

        await Task.Delay(waitDuration ?? TimeSpan.FromMilliseconds(300));
        cts.Cancel();
        try
        {
            await subscribeTask;
        }
        catch (OperationCanceledException)
        {
        }

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public async Task GetEventsRange_MinPrice001_IsValid()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("gift", EventBaseDate.AddHours(1), price: 100),
            Event("gift", EventBaseDate.AddHours(2), price: 50));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, minPrice: 0.01m));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_MinPriceLessThan001_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.GetEventsRange(123456, minPrice: 0.001m);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(badRequest.Value);
        Assert.Contains("minPrice must be at least 0.01", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetEventsRange_MinPriceZero_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.GetEventsRange(123456, minPrice: 0m);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetEventsRange_MinPriceNegative_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.GetEventsRange(123456, minPrice: -1m);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetEventsRange_MinPriceConversion_ToGoldSeeds()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("gift", EventBaseDate.AddHours(1), price: 10),
            Event("gift", EventBaseDate.AddHours(2), price: 11),
            Event("gift", EventBaseDate.AddHours(3), price: 9));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, minPrice: 0.01m));

        Assert.Equal(2, json.GetProperty("total").GetInt32());

        var json2 = Ok(await CreateController(fixture.Database).GetEventsRange(123456, minPrice: 0.011m));
        Assert.Equal(1, json2.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_WithoutMinPrice_ReturnsAll()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("gift", EventBaseDate.AddHours(1), price: 100),
            Event("gift", EventBaseDate.AddHours(2), price: 50),
            Event("danmaku", EventBaseDate.AddHours(3)));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456));

        Assert.Equal(3, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetEventsRange_MinPrice_FiltersGiftOnly()
    {
        var repository = new LiveEventRepository(fixture.Database);
        await SeedEvents(repository,
            Event("gift", EventBaseDate.AddHours(1), price: 100),
            Event("danmaku", EventBaseDate.AddHours(2)),
            Event("gift", EventBaseDate.AddHours(3), price: 50));

        var json = Ok(await CreateController(fixture.Database).GetEventsRange(123456, minPrice: 0.08m));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.All(json.GetProperty("items").EnumerateArray(), i =>
        {
            var type = i.GetProperty("type").GetString();
            Assert.True(type == "danmaku" || type == "gift");
        });
    }

    [Fact]
    public async Task SearchEvents_ReturnsMatchingResults()
    {
        var textIndexRepository = new LiveEventTextIndexRepository(fixture.Database);
        var eventRepository = new LiveEventRepository(fixture.Database);

        // 插入事件
        var event1 = Event("danmaku", EventBaseDate.AddHours(1));
        var event2 = Event("danmaku", EventBaseDate.AddHours(2));
        await eventRepository.InsertAsync(event1);
        await eventRepository.InsertAsync(event2);

        // 插入文本索引
        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event1.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = event1.OccurredAt,
        });
        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event2.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Goodbye World",
            OccurredAt = event2.OccurredAt,
        });

        var json = Ok(await CreateController(fixture.Database).SearchEvents(123456, stringCriteria: "Hello"));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
    }

    [Fact]
    public async Task SearchEvents_EmptyStringCriteria_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.SearchEvents(123456, stringCriteria: "");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(badRequest.Value);
        Assert.Contains("stringCriteria must be non-empty", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SearchEvents_NullStringCriteria_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.SearchEvents(123456, stringCriteria: null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(badRequest.Value);
        Assert.Contains("stringCriteria must be non-empty", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SearchEvents_WhitespaceStringCriteria_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);

        var result = await controller.SearchEvents(123456, stringCriteria: "   ");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(badRequest.Value);
        Assert.Contains("stringCriteria must be non-empty", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SearchEvents_StringCriteriaOver200Chars_ReturnsBadRequest()
    {
        var controller = CreateController(fixture.Database);
        var longCriteria = new string('a', 201);

        var result = await controller.SearchEvents(123456, stringCriteria: longCriteria);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(badRequest.Value);
        Assert.Contains("at most 200 characters", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SearchEvents_StringCriteriaExactly200Chars_IsValid()
    {
        var textIndexRepository = new LiveEventTextIndexRepository(fixture.Database);
        var eventRepository = new LiveEventRepository(fixture.Database);

        var event1 = Event("danmaku", EventBaseDate.AddHours(1));
        await eventRepository.InsertAsync(event1);

        var criteria200 = new string('a', 200);
        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event1.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = criteria200,
            OccurredAt = event1.OccurredAt,
        });

        var json = Ok(await CreateController(fixture.Database).SearchEvents(123456, stringCriteria: criteria200));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SearchEvents_NoMatch_ReturnsEmptyWithTotalZero()
    {
        var textIndexRepository = new LiveEventTextIndexRepository(fixture.Database);
        var eventRepository = new LiveEventRepository(fixture.Database);

        var event1 = Event("danmaku", EventBaseDate.AddHours(1));
        await eventRepository.InsertAsync(event1);

        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event1.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = event1.OccurredAt,
        });

        var json = Ok(await CreateController(fixture.Database).SearchEvents(123456, stringCriteria: "NotExist"));

        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task SearchEvents_PageBeyondRange_ReturnsEmptyWithRealTotal()
    {
        var textIndexRepository = new LiveEventTextIndexRepository(fixture.Database);
        var eventRepository = new LiveEventRepository(fixture.Database);

        var event1 = Event("danmaku", EventBaseDate.AddHours(1));
        await eventRepository.InsertAsync(event1);

        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event1.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = event1.OccurredAt,
        });

        var json = Ok(await CreateController(fixture.Database).SearchEvents(123456, stringCriteria: "Hello", page: 5, pageSize: 20));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task SearchEvents_ResultsSortedByOccurredAtDesc()
    {
        var textIndexRepository = new LiveEventTextIndexRepository(fixture.Database);
        var eventRepository = new LiveEventRepository(fixture.Database);

        var event1 = Event("danmaku", EventBaseDate.AddHours(1));
        var event2 = Event("danmaku", EventBaseDate.AddHours(3));
        var event3 = Event("danmaku", EventBaseDate.AddHours(2));
        await eventRepository.InsertAsync(event1);
        await eventRepository.InsertAsync(event2);
        await eventRepository.InsertAsync(event3);

        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event1.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = event1.OccurredAt,
        });
        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event2.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = event2.OccurredAt,
        });
        await textIndexRepository.InsertAsync(new LiveEventTextIndexRecord
        {
            EventId = event3.Id,
            RoomId = 123456,
            Type = "danmaku",
            Text = "Hello World",
            OccurredAt = event3.OccurredAt,
        });

        var json = Ok(await CreateController(fixture.Database).SearchEvents(123456, stringCriteria: "Hello"));

        Assert.Equal(3, json.GetProperty("total").GetInt32());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal(EventBaseDate.AddHours(3), items[0].GetProperty("occurredAt").GetDateTime());
        Assert.Equal(EventBaseDate.AddHours(2), items[1].GetProperty("occurredAt").GetDateTime());
        Assert.Equal(EventBaseDate.AddHours(1), items[2].GetProperty("occurredAt").GetDateTime());
    }

    [Fact]
    public async Task Subscribe_FirstFrameIsHeartbeat()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        var content = await RunSubscribeAsync(controller, bus, 123456, null, [],
            publishEvents: false);

        Assert.StartsWith("event: heartbeat\n", content);
        Assert.Contains("data: {}\n\n", content);
    }

    [Fact]
    public async Task Subscribe_HeartbeatWithShortInterval()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        var originalInterval = BilibiliLiveController.HeartbeatInterval;
        try
        {
            BilibiliLiveController.HeartbeatInterval = TimeSpan.FromMilliseconds(50);

            var content = await RunSubscribeAsync(controller, bus, 123456, null, [],
                waitDuration: TimeSpan.FromMilliseconds(250), publishEvents: false);

            var heartbeatCount = CountOccurrences(content, "event: heartbeat\n");
            Assert.True(heartbeatCount >= 3, $"Expected at least 3 heartbeats, got {heartbeatCount}");
        }
        finally
        {
            BilibiliLiveController.HeartbeatInterval = originalInterval;
        }
    }

    [Fact]
    public async Task Subscribe_HeartbeatNotAffectedByTypeFilter()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        var originalInterval = BilibiliLiveController.HeartbeatInterval;
        try
        {
            BilibiliLiveController.HeartbeatInterval = TimeSpan.FromMilliseconds(50);

            var content = await RunSubscribeAsync(controller, bus, 123456, ["danmaku"],
            [
                new LiveEvent(123456, "danmaku", null, EventBaseDate.AddHours(1), "{}"),
                new LiveEvent(123456, "gift", null, EventBaseDate.AddHours(2), "{}"),
            ],
            waitDuration: TimeSpan.FromMilliseconds(500));

            Assert.Contains("event: heartbeat\n", content);
            Assert.True(CountOccurrences(content, "event: danmaku\n") >= 1,
                $"Expected at least 1 danmaku event, got {CountOccurrences(content, "event: danmaku\\n")}");
            Assert.DoesNotContain("event: gift\n", content);
        }
        finally
        {
            BilibiliLiveController.HeartbeatInterval = originalInterval;
        }
    }

    [Fact]
    public async Task Subscribe_BusinessEventsAndHeartbeatInterleave()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        var content = await RunSubscribeAsync(controller, bus, 123456, null,
        [
            new LiveEvent(123456, "danmaku", null, EventBaseDate.AddHours(1), "{}"),
            new LiveEvent(123456, "gift", null, EventBaseDate.AddHours(2), "{}"),
        ]);

        Assert.Contains("event: heartbeat\n", content);
        Assert.Contains("event: danmaku\n", content);
        Assert.Contains("event: gift\n", content);
    }

    [Fact]
    public async Task Subscribe_AfterCancelNoMoreHeartbeats()
    {
        var bus = new LiveEventBus();
        var controller = new BilibiliLiveController(
            new LiveStatusRepository(fixture.Database),
            new LiveEventRepository(fixture.Database),
            new LiveSessionRepository(fixture.Database),
            new LiveEventTextIndexRepository(fixture.Database),
            bus);

        using var body = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = body } };
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        using var cts = new CancellationTokenSource();
        var subscribeTask = controller.Subscribe(123456, null, cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        try
        {
            await subscribeTask;
        }
        catch (OperationCanceledException)
        {
        }

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();

        var heartbeatCount = CountOccurrences(content, "event: heartbeat\n");
        Assert.True(heartbeatCount >= 1 && heartbeatCount <= 3,
            $"Expected 1-3 heartbeats after cancel, got {heartbeatCount}");
    }
}
