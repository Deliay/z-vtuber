using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/weibo/subscription")]
public class WeiboSubscriptionController(IWeiboSubscriptionService weiboSubscription) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var subscriptions = await weiboSubscription.GetAllAsync(ct);
        var items = subscriptions.Select(s => new WeiboSubscriptionItemDto(s.Uid, s.SubscribedAt)).ToList();
        return Ok(new { items });
    }

    [HttpPost]
    public async Task<IActionResult> Subscribe([FromBody] WeiboSubscribeRequest request, CancellationToken ct)
    {
        if (request?.Uid is null or <= 0)
        {
            return BadRequest(new { error = "uid must be a positive integer." });
        }

        await weiboSubscription.SubscribeAsync(request.Uid.Value, ct);
        return Ok(new WeiboSubscriptionItemDto(request.Uid.Value, null));
    }

    [HttpDelete("{uid:long}")]
    public async Task<IActionResult> Unsubscribe(long uid, CancellationToken ct)
    {
        var removed = await weiboSubscription.UnsubscribeAsync(uid, ct);
        return removed ? NoContent() : NotFound();
    }
}

public sealed record WeiboSubscribeRequest(long? Uid);

public sealed record WeiboSubscriptionItemDto(long Uid, DateTime? SubscribedAt);
