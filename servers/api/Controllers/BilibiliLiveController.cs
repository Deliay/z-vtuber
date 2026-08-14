using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Live;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/live")]
public class BilibiliLiveController(
    LiveStatusRepository liveStatusRepository,
    LiveEventRepository liveEventRepository,
    ILiveEventBus eventBus) : ControllerBase
{
    [HttpGet("{roomId}/state/all")]
    public async Task<IActionResult> GetAllStates(
        long roomId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (total, items) = await liveStatusRepository.GetPageAsync(roomId, page, pageSize, ct);
        return Ok(new { total, page, pageSize, items = items.Select(ToStateDto) });
    }

    [HttpGet("{roomId}/state/current")]
    public async Task<IActionResult> GetCurrentState(long roomId, CancellationToken ct)
    {
        var record = await liveStatusRepository.GetLatestByRoomIdAsync(roomId, ct);
        return record is null ? NotFound() : Ok(ToStateDto(record));
    }

    [HttpGet("{roomId}/event/range")]
    public async Task<IActionResult> GetEventsRange(
        long roomId,
        [FromQuery] DateTime startedAt,
        [FromQuery] DateTime endedAt,
        CancellationToken ct)
    {
        if (endedAt < startedAt)
        {
            return BadRequest(new { error = "endedAt must not be earlier than startedAt." });
        }

        var items = await liveEventRepository.GetRangeAsync(roomId, startedAt, endedAt, ct);
        return Ok(new { items = items.Select(ToEventDto) });
    }

    [HttpGet("{roomId}/event/subscribe")]
    public async Task Subscribe(long roomId, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var subscription = eventBus.Subscribe(roomId);
        try
        {
            await foreach (var liveEvent in subscription.Reader.ReadAllAsync(ct))
            {
                var data = JsonSerializer.Serialize(ToEventDto(liveEvent));
                await Response.WriteAsync($"event: {liveEvent.Type}\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            eventBus.Unsubscribe(roomId, subscription.Id);
        }
    }

    private static object ToStateDto(LiveStatusRecord record) => new
    {
        roomId = record.RoomId,
        title = record.Title,
        liveStatus = record.LiveStatus,
        liveSessionId = record.LiveSessionId,
        createdAt = record.CreatedAt,
    };

    private static object ToEventDto(LiveEventRecord record) => new
    {
        roomId = record.RoomId,
        type = record.Type,
        occurredAt = record.OccurredAt,
        liveSessionId = record.LiveSessionId,
        payload = BilibiliUserController.ParseRaw(record.Payload),
    };

    private static object ToEventDto(LiveEvent liveEvent) => new
    {
        roomId = liveEvent.RoomId,
        type = liveEvent.Type,
        occurredAt = liveEvent.OccurredAt,
        liveSessionId = liveEvent.LiveSessionId,
        payload = BilibiliUserController.ParseRaw(liveEvent.Payload),
    };
}
