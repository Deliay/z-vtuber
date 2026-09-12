using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/cookie")]
public class BilibiliCookieController(ICookiePoolService cookiePool) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> ListActive(CancellationToken ct)
    {
        var cookies = await cookiePool.GetActiveAsync(ct);
        var items = cookies.Select(ToDto).ToList();
        return Ok(new { items });
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] CookieAddRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Cookie))
        {
            return BadRequest(new { error = "cookie must not be empty." });
        }

        var record = await cookiePool.AddAsync(request.Cookie, ct: ct);
        return Ok(ToDto(record));
    }

    private static object ToDto(CookieRecord record) => new
    {
        id = record.Id.ToString(),
        status = record.Status,
        lastCheckedAt = record.LastCheckedAt,
        lastUsedAt = record.LastUsedAt,
    };
}

public sealed record CookieAddRequest(string Cookie);
