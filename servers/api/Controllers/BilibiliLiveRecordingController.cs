using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/b/live")]
public class BilibiliLiveRecordingController(
    ISubscriptionService subscriptionService,
    AccountBasicInfoRepository accountBasicInfo,
    RecordingStatusRepository recordingStatus,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    internal const string RecordingClientName = "recording";

    /// <summary>recording 状态过期阈值：recording 且 UpdatedAt 超过该时长未刷新则退化为 unknown，避免陈旧残留误导页面。</summary>
    internal static readonly TimeSpan StaleRecordingThreshold = TimeSpan.FromMinutes(3);

    [HttpGet("recording-status")]
    public async Task<IActionResult> ListRecordingStatus(CancellationToken ct)
    {
        var subscriptions = await subscriptionService.GetAllAsync(ct);
        var recordingSubscriptions = subscriptions
            .Where(s => s.Type == SubscriptionType.Recording)
            .ToList();

        var mids = recordingSubscriptions.Select(s => s.Mid).Distinct().ToList();
        var accountInfos = await accountBasicInfo.GetLatestByMidsAsync(mids, ct);

        var roomIds = accountInfos.Values
            .Select(i => i.RoomId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        IReadOnlyList<RecordingStatusRecord> statuses;
        try
        {
            statuses = await recordingStatus.GetByRoomIdsAsync(roomIds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // recording 状态数据面不可用时退化为 unknown，不阻断页面
            statuses = [];
        }

        var statusByRoom = statuses.ToDictionary(s => s.RoomId);

        var items = recordingSubscriptions.Select(s =>
        {
            accountInfos.TryGetValue(s.Mid, out var info);
            var roomId = info is { RoomId: > 0 } ? info.RoomId : (long?)null;
            RecordingStatusRecord? status = null;
            if (roomId.HasValue)
            {
                statusByRoom.TryGetValue(roomId.Value, out status);
            }

            var state = status?.State ?? "unknown";
            if (status is { State: "recording" }
                && DateTime.UtcNow - status.UpdatedAt > StaleRecordingThreshold)
            {
                // 心跳停止（实例崩溃/重启且未能纠正）：读侧退化为未知，不呈现虚假的「录制中」
                state = "unknown";
            }

            return new
            {
                mid = s.Mid,
                roomId,
                state,
                hostName = status?.HostName,
                sessionTitle = status?.SessionTitle,
                files = status?.Files ?? [],
                error = status?.Error,
                updatedAt = status?.UpdatedAt,
                streamer = info is null ? null : new BackstageStreamerDto(BilibiliBackstageController.ExtractName(info.Data)),
            };
        }).ToList();

        return Ok(new { items });
    }

    [HttpGet("{roomId:long}/recordings")]
    public async Task<IActionResult> ListRecordings(long roomId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(RecordingClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"api/{roomId}/recordings", ct);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上游超时（非客户端取消）也应返回契约约定的 502
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return Content(json, "application/json");
        }
    }

    [HttpGet("{roomId:long}/recordings/{fileName}")]
    public async Task<IActionResult> DownloadRecording(long roomId, string fileName, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(RecordingClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                $"api/{roomId}/recordings/{Uri.EscapeDataString(fileName)}",
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上游超时（非客户端取消）也应返回契约约定的 502
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
            }

            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (response.Content.Headers.ContentDisposition is not null)
            {
                Response.Headers.ContentDisposition = response.Content.Headers.ContentDisposition.ToString();
            }
            else
            {
                Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            }

            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }

    [HttpGet("{roomId:long}/live-stream/audio")]
    public async Task<IActionResult> ProxyAudio(long roomId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(RecordingClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                $"api/{roomId}/stream/audio",
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上游超时（非客户端取消）也应返回契约约定的 502
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "recording service unavailable" });
            }

            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/x-flv";
            Response.Headers.CacheControl = "no-store";
            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }
}
