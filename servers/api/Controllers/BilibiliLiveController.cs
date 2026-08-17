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
    LiveSessionRepository liveSessionRepository,
    ILiveEventBus eventBus) : ControllerBase
{
    [HttpGet("{roomId}/state/all")]
    public async Task<IActionResult> GetAllStates(
        long roomId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? startedAt = null,
        [FromQuery] DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        if (BilibiliUserController.BadTimeRange(startedAt, endedAt))
        {
            return BadRequest(new { error = "startedAt must not be later than endedAt." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (total, items) = await liveStatusRepository.GetPageAsync(roomId, page, pageSize, startedAt, endedAt, ct);
        return Ok(new { total, page, pageSize, items = items.Select(ToStateDto) });
    }

    [HttpGet("{roomId}/state/current")]
    public async Task<IActionResult> GetCurrentState(long roomId, CancellationToken ct)
    {
        var record = await liveStatusRepository.GetLatestByRoomIdAsync(roomId, ct);
        return record is null ? NotFound() : Ok(ToStateDto(record));
    }

    [HttpGet("{roomId}/history")]
    public async Task<IActionResult> GetHistory(
        long roomId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? startedAt = null,
        [FromQuery] DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        if (BilibiliUserController.BadTimeRange(startedAt, endedAt))
        {
            return BadRequest(new { error = "startedAt must not be later than endedAt." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (total, items) = await liveSessionRepository.GetPageAsync(roomId, page, pageSize, startedAt, endedAt, ct);
        return Ok(new { total, page, pageSize, items = items.Select(ToSessionDto) });
    }

    [HttpGet("{roomId}/event/range")]
    public async Task<IActionResult> GetEventsRange(
        long roomId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string[]? type = null,
        [FromQuery] DateTime? startedAt = null,
        [FromQuery] DateTime? endedAt = null,
        [FromQuery] decimal? minPrice = null,
        CancellationToken ct = default)
    {
        if (BilibiliUserController.BadTimeRange(startedAt, endedAt))
        {
            return BadRequest(new { error = "startedAt must not be later than endedAt." });
        }

        if (minPrice.HasValue && minPrice.Value < 0.01m)
        {
            return BadRequest(new { error = "minPrice must be at least 0.01." });
        }

        if (minPrice.HasValue && minPrice.Value > 9_000_000_000_000_000m)
        {
            return BadRequest(new { error = "minPrice is too large." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        long? minPriceGoldSeeds = null;
        if (minPrice.HasValue)
        {
            minPriceGoldSeeds = (long)Math.Ceiling(minPrice.Value * 1000m);
        }

        var types = NormalizeTypes(type);
        var (total, items) = await liveEventRepository.GetPageAsync(roomId, page, pageSize, types, startedAt, endedAt, minPriceGoldSeeds, ct);
        return Ok(new { total, page, pageSize, items = items.Select(ToEventDto) });
    }

    [HttpGet("{roomId}/event/subscribe")]
    public async Task Subscribe(long roomId, [FromQuery] string[]? type = null, CancellationToken ct = default)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var types = NormalizeTypes(type);

        var subscription = eventBus.Subscribe(roomId);
        try
        {
            await foreach (var liveEvent in subscription.Reader.ReadAllAsync(ct))
            {
                if (types is { Count: > 0 } && !types.Contains(liveEvent.Type))
                {
                    continue;
                }

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

    private static HashSet<string>? NormalizeTypes(string[]? types)
    {
        if (types is null || types.Length == 0)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!string.IsNullOrWhiteSpace(type))
            {
                set.Add(type);
            }
        }

        return set.Count == 0 ? null : set;
    }

    private static object ToStateDto(LiveStatusRecord record) => new
    {
        roomId = record.RoomId,
        title = record.Title,
        liveStatus = record.LiveStatus,
        liveSessionId = record.LiveSessionId,
        createdAt = record.CreatedAt,
    };

    private static object ToSessionDto(LiveSessionRecord record) => new
    {
        liveId = record.LiveId,
        roomId = record.RoomId,
        title = record.Title,
        coverUrl = record.CoverUrl,
        parentArea = record.ParentArea,
        area = record.Area,
        isFinish = record.IsFinish,
        startDate = record.StartDate,
        stopDate = record.StopDate,
        danmakuCount = record.DanmakuCount,
        totalIncome = record.TotalIncome,
        watchCount = record.WatchCount,
        likeCount = record.LikeCount,
        payCount = record.PayCount,
        maxOnlineCount = record.MaxOnlineCount,
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
