using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/weibo/user")]
public class WeiboUserController(WeiboTimelineRepository weiboTimelineRepository) : ControllerBase
{
    [HttpGet("{uid:long}/timeline")]
    public async Task<IActionResult> GetTimeline(
        long uid,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? startedAt = null,
        [FromQuery] DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        if (BadTimeRange(startedAt, endedAt))
        {
            return BadRequest(new { error = "startedAt must not be later than endedAt." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (total, items) = await weiboTimelineRepository.GetPageAsync(uid, page, pageSize, startedAt, endedAt, ct);
        var result = items.Select(i => new
        {
            i.Uid,
            i.MblogId,
            i.FetchedAt,
            Data = ParseRaw(i.Data),
        });

        return Ok(new { total, page, pageSize, items = result });
    }

    internal static JsonNode? ParseRaw(string? raw) =>
        string.IsNullOrEmpty(raw) ? null : JsonNode.Parse(raw);

    internal static bool BadTimeRange(DateTime? startedAt, DateTime? endedAt) =>
        startedAt.HasValue && endedAt.HasValue && endedAt.Value < startedAt.Value;
}
