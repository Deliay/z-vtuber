using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/live")]
public class BilibiliLiveController(
    LiveStatusRepository liveStatusRepository,
    LiveEventRepository liveEventRepository,
    LiveSessionRepository liveSessionRepository,
    LiveEventTextIndexRepository textIndexRepository,
    ILiveEventBus eventBus) : ControllerBase
{
    internal static TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
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

    [HttpGet("{roomId}/event/search")]
    public async Task<IActionResult> SearchEvents(
        long roomId,
        [FromQuery] string? stringCriteria = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        // 校验 stringCriteria：缺失、空串、纯空白或超过 200 字符 → 400
        if (string.IsNullOrWhiteSpace(stringCriteria) || stringCriteria.Length > 200)
        {
            return BadRequest(new { error = "stringCriteria must be non-empty and at most 200 characters." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // 搜索文本索引集合
        var (total, textIndexRecords) = await textIndexRepository.SearchAsync(roomId, stringCriteria, page, pageSize, ct);

        if (textIndexRecords.Count == 0)
        {
            return Ok(new { total, page, pageSize, items = Array.Empty<object>() });
        }

        // 获取对应的事件记录
        var eventIds = textIndexRecords.Select(r => r.EventId).ToList();
        var events = await liveEventRepository.GetByIdsAsync(eventIds, ct);
        var eventDict = events.ToDictionary(e => e.Id);

        // 按索引记录的 OccurredAt/Id 顺序还原排序
        var sortedEvents = textIndexRecords
            .Where(r => eventDict.ContainsKey(r.EventId))
            .Select(r => eventDict[r.EventId])
            .ToList();

        return Ok(new { total, page, pageSize, items = sortedEvents.Select(ToEventDto) });
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
            await WriteHeartbeatAsync(ct);

            var reader = subscription.Reader;
            while (!ct.IsCancellationRequested)
            {
                var readTask = reader.WaitToReadAsync(ct).AsTask();
                var delayTask = Task.Delay(HeartbeatInterval, ct);

                var completedTask = await Task.WhenAny(readTask, delayTask);

                if (completedTask == delayTask)
                {
                    await WriteHeartbeatAsync(ct);
                    continue;
                }

                if (!await readTask)
                {
                    break;
                }

                if (reader.TryRead(out var liveEvent))
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
        }
        finally
        {
            eventBus.Unsubscribe(roomId, subscription.Id);
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken ct)
    {
        await Response.WriteAsync("event: heartbeat\n", ct);
        await Response.WriteAsync("data: {}\n\n", ct);
        await Response.Body.FlushAsync(ct);
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
