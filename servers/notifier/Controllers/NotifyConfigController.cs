using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Notifier.Satori;

namespace Z.Vtuber.Notifier.Controllers;

[ApiController]
[Route("api/notify")]
public sealed class NotifyConfigController : ControllerBase
{
    private readonly INotificationSubscriptionRepository _subscriptionRepository;
    private readonly ISatoriApiClient _satoriClient;
    private readonly ILogger<NotifyConfigController> _logger;

    public NotifyConfigController(
        INotificationSubscriptionRepository subscriptionRepository,
        ISatoriApiClient satoriClient,
        ILogger<NotifyConfigController> logger)
    {
        _subscriptionRepository = subscriptionRepository;
        _satoriClient = satoriClient;
        _logger = logger;
    }

    [HttpGet("channels")]
    public async Task<IActionResult> GetChannels(CancellationToken ct)
    {
        try
        {
            var platform = _satoriClient.GetCachedPlatform() ?? "satori";
            var guilds = await _satoriClient.GetGuildListAsync(ct);
            var allChannels = new List<NotifyChannelResponse>();

            foreach (var guild in guilds)
            {
                try
                {
                    var channels = await _satoriClient.GetChannelListAsync(guild.Id, ct);
                    foreach (var channel in channels)
                    {
                        allChannels.Add(new NotifyChannelResponse
                        {
                            Platform = platform,
                            GuildId = guild.Id,
                            GuildName = guild.Name,
                            ChannelId = channel.Id,
                            ChannelName = channel.Name,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to list channels for guild {GuildId}; continuing.", guild.Id);
                }
            }

            return Ok(new { items = allChannels });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list channels");
            return StatusCode(503, new { message = "Satori 不可达或登录发现失败" });
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
            Platform = s.Platform,
            GuildId = s.GuildId,
            GuildName = s.GuildName,
            ChannelId = s.ChannelId,
            ChannelName = s.ChannelName,
            CreatedAt = s.CreatedAt,
        }).ToList();

        return Ok(new { items });
    }

    [HttpPost("subscriptions")]
    public async Task<IActionResult> CreateSubscription([FromBody] CreateNotificationSubscriptionRequest request, CancellationToken ct)
    {
        if (request.EventType != "cookie_invalid")
        {
            return BadRequest(new { message = "eventType 非法，当前仅支持 cookie_invalid" });
        }

        // 检查是否已存在
        var existing = await _subscriptionRepository.GetByEventTypeAsync(request.EventType, ct);
        var duplicate = existing.FirstOrDefault(s =>
            s.Platform == request.Platform &&
            s.ChannelId == request.ChannelId);

        if (duplicate is not null)
        {
            return Ok(new NotificationSubscriptionResponse
            {
                Id = duplicate.Id.ToString(),
                EventType = duplicate.EventType,
                Platform = duplicate.Platform,
                GuildId = duplicate.GuildId,
                GuildName = duplicate.GuildName,
                ChannelId = duplicate.ChannelId,
                ChannelName = duplicate.ChannelName,
                CreatedAt = duplicate.CreatedAt,
            });
        }

        var record = new NotificationSubscriptionRecord
        {
            EventType = request.EventType,
            Platform = request.Platform,
            GuildId = request.GuildId,
            GuildName = request.GuildName,
            ChannelId = request.ChannelId,
            ChannelName = request.ChannelName,
        };

        var inserted = await _subscriptionRepository.InsertAsync(record, ct);

        return CreatedAtAction(nameof(GetSubscriptions), new NotificationSubscriptionResponse
        {
            Id = inserted.Id.ToString(),
            EventType = inserted.EventType,
            Platform = inserted.Platform,
            GuildId = inserted.GuildId,
            GuildName = inserted.GuildName,
            ChannelId = inserted.ChannelId,
            ChannelName = inserted.ChannelName,
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

public sealed class NotifyChannelResponse
{
    public string Platform { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string? GuildName { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string? ChannelName { get; set; }
}

public sealed class NotificationSubscriptionResponse
{
    public string Id { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string? GuildName { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string? ChannelName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CreateNotificationSubscriptionRequest
{
    public string EventType { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string? GuildName { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string? ChannelName { get; set; }
}
