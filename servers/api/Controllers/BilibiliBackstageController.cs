using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/backstage")]
public class BilibiliBackstageController(
    ISubscriptionService subscriptionService,
    ICookiePoolService cookiePool,
    AccountBasicInfoRepository accountBasicInfo,
    ICookieAccountInfoRepository cookieAccountInfo) : ControllerBase
{
    [HttpGet("subscription")]
    public async Task<IActionResult> ListSubscriptions(CancellationToken ct)
    {
        var subscriptions = await subscriptionService.GetAllAsync(ct);
        var mids = subscriptions.Select(s => s.Mid).Distinct().ToList();
        var accountInfos = await accountBasicInfo.GetLatestByMidsAsync(mids, ct);

        var items = subscriptions.Select(s =>
        {
            var streamer = accountInfos.TryGetValue(s.Mid, out var info)
                ? new BackstageStreamerDto(ExtractName(info.Data))
                : null;
            return new
            {
                s.Mid,
                s.Type,
                s.SubscribedAt,
                streamer,
            };
        }).ToList();

        return Ok(new { items });
    }

    [HttpGet("cookie")]
    public async Task<IActionResult> ListCookies(CancellationToken ct)
    {
        var cookies = await cookiePool.GetActiveAsync(ct);
        var cookieIds = cookies.Select(c => c.Id).ToList();
        var accountInfos = await cookieAccountInfo.GetByCookieIdsAsync(cookieIds, ct);

        var items = cookies.Select(c =>
        {
            var account = accountInfos.TryGetValue(c.Id, out var info)
                ? new BackstageAccountDto(info.Uname)
                : null;
            return new
            {
                id = c.Id.ToString(),
                status = c.Status,
                lastCheckedAt = c.LastCheckedAt,
                lastUsedAt = c.LastUsedAt,
                account,
            };
        }).ToList();

        return Ok(new { items });
    }

    internal static string? ExtractName(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (Exception)
        {
            return null;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        var nameNode = obj["uName"];
        return nameNode is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
    }
}

public sealed record BackstageStreamerDto(string? Name);
public sealed record BackstageAccountDto(string? Name);