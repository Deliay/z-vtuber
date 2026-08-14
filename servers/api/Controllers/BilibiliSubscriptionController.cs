using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Services;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/subscription")]
public class BilibiliSubscriptionController(ISubscriptionService subscription) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var mids = await subscription.GetMidsAsync(ct);
        var items = mids.Select(mid => new SubscriptionItemDto(mid, null)).ToList();
        return Ok(new { items });
    }

    [HttpPost]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest request, CancellationToken ct)
    {
        if (request?.Mid is null or <= 0)
        {
            return BadRequest(new { error = "mid must be a positive integer." });
        }

        await subscription.SubscribeAsync(request.Mid, ct);
        return Ok(new SubscriptionItemDto(request.Mid, null));
    }

    [HttpDelete("{mid}")]
    public async Task<IActionResult> Unsubscribe(long mid, CancellationToken ct)
    {
        var removed = await subscription.UnsubscribeAsync(mid, ct);
        return removed ? NoContent() : NotFound();
    }
}

public sealed record SubscribeRequest(long Mid);

public sealed record SubscriptionItemDto(long Mid, DateTime? SubscribedAt);
