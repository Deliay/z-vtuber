using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/live")]
public class BilibiliLiveGuardController(LiveGuardRepository liveGuardRepository) : ControllerBase
{
    [HttpGet("{roomId}/guard")]
    public async Task<IActionResult> GetOnboardGuardUids(
        long roomId,
        [FromQuery] string? startedAt = null,
        [FromQuery] string? endedAt = null,
        [FromQuery] string? mode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(startedAt))
        {
            return BadRequest(new { error = "startedAt is required." });
        }

        if (string.IsNullOrWhiteSpace(endedAt))
        {
            return BadRequest(new { error = "endedAt is required." });
        }

        if (!DateOnly.TryParseExact(startedAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
        {
            return BadRequest(new { error = "startedAt must be in yyyy-MM-dd format." });
        }

        if (!DateOnly.TryParseExact(endedAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            return BadRequest(new { error = "endedAt must be in yyyy-MM-dd format." });
        }

        if (startDate > endDate)
        {
            return BadRequest(new { error = "startedAt must not be later than endedAt." });
        }

        var days = endDate.DayNumber - startDate.DayNumber + 1;
        if (days > 366)
        {
            return BadRequest(new { error = "Date range must not exceed 366 days." });
        }

        if (!string.IsNullOrWhiteSpace(mode) && mode != "any" && mode != "all")
        {
            return BadRequest(new { error = "mode must be 'any' or 'all'." });
        }

        var requireAllDays = mode == "all";

        var dates = new List<string>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            dates.Add(date.ToString("yyyy-MM-dd"));
        }

        var entries = await liveGuardRepository.GetOnboardGuardEntriesAsync(roomId, dates, requireAllDays, ct);

        var items = entries.Select(e => new
        {
            uid = e.Uid,
            userName = e.UserName,
            guardLevel = e.GuardLevel,
            medalName = e.MedalName,
            medalLevel = e.MedalLevel,
        }).ToList();

        return Ok(new { total = items.Count, items });
    }
}
