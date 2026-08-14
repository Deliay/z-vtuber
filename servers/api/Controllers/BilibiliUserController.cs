using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Persistence;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/user")]
public class BilibiliUserController(
    AccountBasicInfoRepository accountRepository,
    SpaceTimelineRepository spaceTimelineRepository) : ControllerBase
{
    [HttpGet("{mid}")]
    public async Task<IActionResult> GetAccountBasicInfo(long mid, CancellationToken ct)
    {
        var record = await accountRepository.GetLatestByMidAsync(mid, ct);
        if (record is null)
        {
            return NotFound();
        }

        return Content(record.Data, "application/json", Encoding.UTF8);
    }

    [HttpGet("{mid}/space")]
    public async Task<IActionResult> GetSpaceTimeline(
        long mid,
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

        var (total, items) = await spaceTimelineRepository.GetPageAsync(mid, page, pageSize, startedAt, endedAt, ct);
        var result = items.Select(i => new
        {
            i.Mid,
            i.DynamicId,
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
