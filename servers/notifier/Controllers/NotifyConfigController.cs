using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Notifier.Milky;

namespace Z.Vtuber.Notifier.Controllers;

[ApiController]
[Route("api/notify")]
public sealed class NotifyConfigController : ControllerBase
{
    private readonly INotificationSubscriptionRepository _subscriptionRepository;
    private readonly IMilkyApiClient _milkyClient;
    private readonly ILogger<NotifyConfigController> _logger;

    public NotifyConfigController(
        INotificationSubscriptionRepository subscriptionRepository,
        IMilkyApiClient milkyClient,
        ILogger<NotifyConfigController> logger)
    {
        _subscriptionRepository = subscriptionRepository;
        _milkyClient = milkyClient;
        _logger = logger;
    }

    [HttpGet("groups")]
    public async Task<IActionResult> GetGroups(CancellationToken ct)
    {
        try
        {
            // 先探活
            await _milkyClient.GetLoginInfoAsync(ct);
            var groups = await _milkyClient.GetGroupListAsync(ct);
            var items = groups.Select(g => new NotifyGroupResponse
            {
                GroupId = g.GroupId.ToString(),
                GroupName = g.GroupName,
            }).ToList();

            return Ok(new { items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list groups");
            return StatusCode(503, new { message = "Milky 不可达或机器人未登录" });
        }
    }

    [HttpGet("subscriptions")]
    public async Task<IActionResult> GetSubscriptions(CancellationToken ct)
    {
        var subscriptions = await _subscriptionRepository.ListAsync(ct);
        var items = subscriptions.Select(s => new NotificationSubscriptionResponse
        {
            Id = s.Id.ToString(),
            EventType = s.EventType,
            GroupId = s.GroupId,
            GroupName = s.GroupName,
            CreatedAt = s.CreatedAt,
        }).ToList();

        return Ok(new { items });
    }

    private static readonly HashSet<string> AllowedEventTypes =
    [
        "bilibili_cookie_invalid",
        "weibo_cookie_invalid",
    ];

    [HttpPost("subscriptions")]
    public async Task<IActionResult> CreateSubscription([FromBody] CreateNotificationSubscriptionRequest request, CancellationToken ct)
    {
        if (!AllowedEventTypes.Contains(request.EventType))
        {
            return BadRequest(new { message = "eventType 非法，当前仅支持 bilibili_cookie_invalid 或 weibo_cookie_invalid" });
        }

        // 校验 groupId 可解析为 int64
        if (!long.TryParse(request.GroupId, out _))
        {
            return BadRequest(new { message = "groupId 非法，必须为有效的群号（int64）" });
        }

        // 检查是否已存在
        var existing = await _subscriptionRepository.GetByEventTypeAsync(request.EventType, ct);
        var duplicate = existing.FirstOrDefault(s => s.GroupId == request.GroupId);

        if (duplicate is not null)
        {
            return Ok(new NotificationSubscriptionResponse
            {
                Id = duplicate.Id.ToString(),
                EventType = duplicate.EventType,
                GroupId = duplicate.GroupId,
                GroupName = duplicate.GroupName,
                CreatedAt = duplicate.CreatedAt,
            });
        }

        var record = new NotificationSubscriptionRecord
        {
            EventType = request.EventType,
            GroupId = request.GroupId,
            GroupName = request.GroupName,
        };

        var inserted = await _subscriptionRepository.InsertAsync(record, ct);

        return CreatedAtAction(nameof(GetSubscriptions), new NotificationSubscriptionResponse
        {
            Id = inserted.Id.ToString(),
            EventType = inserted.EventType,
            GroupId = inserted.GroupId,
            GroupName = inserted.GroupName,
            CreatedAt = inserted.CreatedAt,
        });
    }

    [HttpDelete("subscriptions/{id}")]
    public async Task<IActionResult> DeleteSubscription(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var objectId))
        {
            return NotFound(new { message = "订阅不存在" });
        }

        var deleted = await _subscriptionRepository.DeleteAsync(objectId, ct);
        if (!deleted)
        {
            return NotFound(new { message = "订阅不存在" });
        }

        return NoContent();
    }
}

public sealed class NotifyGroupResponse
{
    public string GroupId { get; set; } = string.Empty;
    public string? GroupName { get; set; }
}

public sealed class NotificationSubscriptionResponse
{
    public string Id { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string? GroupName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CreateNotificationSubscriptionRequest
{
    public string EventType { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string? GroupName { get; set; }
}