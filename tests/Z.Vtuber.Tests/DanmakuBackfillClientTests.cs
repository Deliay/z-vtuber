using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

public sealed class DanmakuBackfillClientTests
{
    private static DanmakuBackfillClient Create(StubHttpHandler handler, int maxPages = 3)
    {
        var options = AuthTestOptions.Danmaku();
        options.MaxBackfillPages = maxPages;
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        return new DanmakuBackfillClient(http, AuthTestOptions.Opt(options), NullLogger<DanmakuBackfillClient>.Instance);
    }

    private static string Item(string roomId, string occurredAt, string token, long uid, bool numericRoom = false, bool payloadAsString = false)
    {
        var payload = AuthTestData.DanmakuPayload(token, uid);
        var payloadJson = payloadAsString ? JsonSerializer.Serialize(payload) : payload;
        var room = numericRoom ? roomId : $"\"{roomId}\"";
        return $"{{\"roomId\":{room},\"type\":\"danmaku\",\"occurredAt\":\"{occurredAt}\",\"liveSessionId\":null,\"payload\":{payloadJson}}}";
    }

    [Fact]
    public async Task FetchesAndOrdersByOccurredAt_AndAcceptsNumberOrStringRoomId()
    {
        var responses = new Queue<string>([
            $$"""{"total":2,"page":1,"pageSize":100,"items":[{{Item("30066", "2026-11-19T12:00:02Z", "A1b2C3d4E5", 1, numericRoom: true)}},{{Item("30066", "2026-11-19T12:00:01Z", "A1b2C3d4E6", 2)}}]}"""
        ]);
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(responses.Dequeue()));
        var result = await Create(handler).FetchAsync(
            "30066", new DateTime(2026, 11, 19, 11, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 19, 12, 1, 0, DateTimeKind.Utc), default);

        Assert.False(result.Truncated);
        Assert.Equal(2, result.Events.Count);
        Assert.True(result.Events[0].OccurredAt < result.Events[1].OccurredAt);
        Assert.All(result.Events, e => Assert.Equal("30066", e.RoomId));
    }

    [Fact]
    public async Task FiltersEventsFromOtherRooms()
    {
        var response = $$"""{"total":2,"page":1,"pageSize":100,"items":[{{Item("30066", "2026-11-19T12:00:01Z", "A1b2C3d4E5", 1)}},{{Item("12345", "2026-11-19T12:00:02Z", "A1b2C3d4E5", 1)}}]}""";
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(response));
        var result = await Create(handler).FetchAsync(
            "30066", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, default);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task TruncatesAtMaxPages()
    {
        var items = string.Join(",", Enumerable.Range(0, 100)
            .Select(i => Item("30066", "2026-11-19T12:00:00Z", "A1b2C3d4E5", i + 1)));
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(
            $$"""{"total":200,"page":1,"pageSize":100,"items":[{{items}}]}"""));
        var result = await Create(handler, maxPages: 1).FetchAsync(
            "30066", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, default);
        Assert.True(result.Truncated);
        Assert.Equal(100, result.Events.Count);
    }

    [Fact]
    public async Task HttpFailure_ReturnsEmptyWithoutThrowing()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var result = await Create(handler).FetchAsync(
            "30066", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, default);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task BadJson_ReturnsEmptyWithoutThrowing()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("not-json"));
        var result = await Create(handler).FetchAsync(
            "30066", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, default);
        Assert.Empty(result.Events);
    }
}
