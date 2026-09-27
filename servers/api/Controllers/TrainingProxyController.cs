using Microsoft.AspNetCore.Mvc;

namespace Z.Vtuber.Controllers;

/// <summary>
/// 将 <c>/api/training/**</c> 代理至 training 服务（named client <c>TrainingService</c>）。
/// 参考 <see cref="RecordingTranscriptProxyController"/> 的 <c>ForwardAsync</c>：
/// query 原样透传、POST/PUT body 与 Content-Type 透传、上游状态码/响应体/Content-Disposition 透传，
/// 上游不可达或超时返回 502 <c>{"error":"training service unavailable"}</c>。
/// </summary>
[ApiController]
[Route("api")]
public class TrainingProxyController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    internal const string TrainingClientName = "TrainingService";

    [HttpGet("training/datasets")]
    public Task<IActionResult> ListDatasets([FromQuery] string? type, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/training/datasets", ct);

    [HttpPost("training/datasets")]
    public Task<IActionResult> CreateDataset(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, "api/training/datasets", ct);

    [HttpPut("training/datasets/{datasetId}")]
    public Task<IActionResult> RenameDataset(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Put, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}", ct);

    [HttpDelete("training/datasets/{datasetId}")]
    public Task<IActionResult> DeleteDataset(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Delete, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}", ct);

    [HttpGet("training/datasets/{datasetId}/items")]
    public Task<IActionResult> ListItems(string datasetId, [FromQuery] string? audioType, [FromQuery] string? source, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/items", ct);

    [HttpPost("training/datasets/{datasetId}/items/import")]
    public Task<IActionResult> ImportItems(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/items/import", ct);

    [HttpPost("training/datasets/{datasetId}/items/batch-delete")]
    public Task<IActionResult> BatchDeleteItems(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/items/batch-delete", ct);

    [HttpPut("training/datasets/{datasetId}/items/{itemId}")]
    public Task<IActionResult> UpdateItem(string datasetId, string itemId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Put, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/items/{Uri.EscapeDataString(itemId)}", ct);

    private async Task<IActionResult> ForwardAsync(HttpMethod method, string upstreamPath, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(TrainingClientName);
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
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "training service unavailable" });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上游超时（非客户端取消）也应返回契约约定的 502
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "training service unavailable" });
        }

        using (response)
        {
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            if (response.Headers.CacheControl is not null)
            {
                Response.Headers.CacheControl = response.Headers.CacheControl.ToString();
            }

            var contentDisposition = response.Content.Headers.ContentDisposition?.ToString();
            if (!string.IsNullOrEmpty(contentDisposition))
            {
                Response.Headers.ContentDisposition = contentDisposition;
            }

            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }
}
