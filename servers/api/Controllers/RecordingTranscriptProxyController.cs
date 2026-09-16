using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Z.Vtuber.Controllers;

/// <summary>
/// 将 <c>/api/transcripts/**</c> 与 <c>/api/recordings/**</c> 流式代理至 recording-transcript-api。
/// 参考 <see cref="BilibiliLiveRecordingController"/>：<c>ResponseHeadersRead</c> + <c>CopyToAsync</c>，上游不可达返回 502。
/// SSE 端点不缓冲并透传 <c>text/event-stream</c>。
/// </summary>
[ApiController]
[Route("api")]
public class RecordingTranscriptProxyController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    internal const string TranscriptClientName = "TranscriptService";

    [HttpGet("recordings")]
    public Task<IActionResult> ListRecordings(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/recordings", ct);

    [HttpGet("recordings/{roomId:long}/transcripts")]
    public Task<IActionResult> GetRoomTranscripts(long roomId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, $"api/recordings/{roomId}/transcripts", ct);

    [HttpPost("recordings/{roomId:long}/transcripts/settings")]
    public Task<IActionResult> UpdateRoomTranscriptSettings(long roomId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/recordings/{roomId}/transcripts/settings", ct);

    [HttpPost("transcripts/enqueue")]
    public Task<IActionResult> EnqueueTranscript(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, "api/transcripts/enqueue", ct);

    [HttpGet("transcripts/task")]
    public Task<IActionResult> ListTranscriptTasks(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/transcripts/task", ct);

    [HttpGet("transcripts/task/{taskId}/sse")]
    public Task<IActionResult> StreamTranscriptSegments(string taskId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, $"api/transcripts/task/{Uri.EscapeDataString(taskId)}/sse", ct, streaming: true);

    [HttpGet("transcripts/task/{taskId}")]
    public Task<IActionResult> GetTranscriptTask(string taskId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, $"api/transcripts/task/{Uri.EscapeDataString(taskId)}", ct);

    [HttpPost("transcripts/task/{taskId}/retry")]
    public Task<IActionResult> RetryTranscriptTask(string taskId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/transcripts/task/{Uri.EscapeDataString(taskId)}/retry", ct);

    [HttpGet("transcripts/settings")]
    public Task<IActionResult> ListTranscriptSettings(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/transcripts/settings", ct);

    [HttpGet("transcripts/models")]
    public Task<IActionResult> ListTranscriptModels(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/transcripts/models", ct);

    [HttpPost("transcripts/models")]
    public Task<IActionResult> CreateTranscriptModel(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, "api/transcripts/models", ct);

    [HttpPut("transcripts/models/{modelId}")]
    public Task<IActionResult> UpdateTranscriptModel(string modelId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Put, $"api/transcripts/models/{Uri.EscapeDataString(modelId)}", ct);

    private async Task<IActionResult> ForwardAsync(
        HttpMethod method, string upstreamPath, CancellationToken ct, bool streaming = false)
    {
        var client = httpClientFactory.CreateClient(TranscriptClientName);
        var target = upstreamPath + Request.QueryString.Value;

        using var request = new HttpRequestMessage(method, target);

        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            request.Content = new StreamContent(Request.Body);
            if (!string.IsNullOrEmpty(Request.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "transcript service unavailable" });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上游超时（非客户端取消）也应返回契约约定的 502
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "transcript service unavailable" });
        }

        using (response)
        {
            Response.StatusCode = (int)response.StatusCode;

            var contentType = response.Content.Headers.ContentType?.ToString();
            var isEventStream = streaming
                || (contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) ?? false);

            Response.ContentType = contentType ?? "application/json";

            if (isEventStream)
            {
                Response.Headers.CacheControl = "no-cache";
                Response.Headers.Connection = "keep-alive";
                Response.Headers["X-Accel-Buffering"] = "no";
                HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            }
            else if (response.Headers.CacheControl is not null)
            {
                Response.Headers.CacheControl = response.Headers.CacheControl.ToString();
            }

            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }
}
