using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/subscription")]
public class BilibiliSubscriptionController(ISubscriptionService subscription) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var subscriptions = await subscription.GetAllAsync(ct);
        var items = subscriptions.Select(s => new SubscriptionItemDto(s.Mid, s.Type, s.SubscribedAt)).ToList();
        return Ok(new { items });
    }

    [HttpPost]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest request, CancellationToken ct)
    {
        if (request?.Mid is null or <= 0)
        {
            return BadRequest(new { error = "mid must be a positive integer." });
        }

        if (string.IsNullOrEmpty(request.Type) || !Enum.TryParse<SubscriptionType>(request.Type, ignoreCase: true, out var type))
        {
            return BadRequest(new { error = "type is required and must be one of: danmaku, dynamic." });
        }

        await subscription.SubscribeAsync(request.Mid, type, ct);
        return Ok(new SubscriptionItemDto(request.Mid, type, null));
    }

    [HttpDelete("{mid}")]
    public async Task<IActionResult> Unsubscribe(long mid, [FromQuery] string type, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(type) || !Enum.TryParse<SubscriptionType>(type, ignoreCase: true, out var subscriptionType))
        {
            return BadRequest(new { error = "type is required and must be one of: danmaku, dynamic." });
        }

        var removed = await subscription.UnsubscribeAsync(mid, subscriptionType, ct);
        return removed ? NoContent() : NotFound();
    }
}

public sealed record SubscribeRequest(long Mid, string? Type);

public sealed record SubscriptionItemDto(long Mid, SubscriptionType Type, DateTime? SubscribedAt);
