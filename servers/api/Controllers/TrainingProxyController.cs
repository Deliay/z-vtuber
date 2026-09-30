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

    [HttpGet("training/providers")]
    public Task<IActionResult> ListProviders(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/training/providers", ct);

    [HttpPost("training/providers")]
    public Task<IActionResult> CreateProvider(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, "api/training/providers", ct);

    [HttpPut("training/providers/{providerId}")]
    public Task<IActionResult> UpdateProvider(string providerId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Put, $"api/training/providers/{Uri.EscapeDataString(providerId)}", ct);

    [HttpDelete("training/providers/{providerId}")]
    public Task<IActionResult> DeleteProvider(string providerId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Delete, $"api/training/providers/{Uri.EscapeDataString(providerId)}", ct);

    [HttpGet("training/providers/{providerId}/models/discover")]
    public Task<IActionResult> DiscoverProviderModels(string providerId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, $"api/training/providers/{Uri.EscapeDataString(providerId)}/models/discover", ct);

    [HttpPost("training/providers/{providerId}/models/import")]
    public Task<IActionResult> ImportProviderModels(string providerId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/providers/{Uri.EscapeDataString(providerId)}/models/import", ct);

    [HttpGet("training/models")]
    public Task<IActionResult> ListModels([FromQuery] bool? enabled, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/training/models", ct);

    [HttpPost("training/models")]
    public Task<IActionResult> CreateModel(CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, "api/training/models", ct);

    [HttpPut("training/models/{modelId}")]
    public Task<IActionResult> UpdateModel(string modelId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Put, $"api/training/models/{Uri.EscapeDataString(modelId)}", ct);

    [HttpDelete("training/models/{modelId}")]
    public Task<IActionResult> DeleteModel(string modelId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Delete, $"api/training/models/{Uri.EscapeDataString(modelId)}", ct);

    [HttpPost("training/datasets/{datasetId}/items/{itemId}/recognize")]
    public Task<IActionResult> RecognizeItem(string datasetId, string itemId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/items/{Uri.EscapeDataString(itemId)}/recognize", ct);

    [HttpPost("training/datasets/{datasetId}/items/emotion-tasks")]
    public Task<IActionResult> CreateEmotionTasks(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/items/emotion-tasks", ct);

    [HttpGet("training/emotion-tasks")]
    public Task<IActionResult> ListEmotionTasks(
        [FromQuery] string? datasetId, [FromQuery] string? status, [FromQuery] int? limit, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Get, "api/training/emotion-tasks", ct);

    [HttpPost("training/emotion-tasks/{taskId}/retry")]
    public Task<IActionResult> RetryEmotionTask(string taskId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/emotion-tasks/{Uri.EscapeDataString(taskId)}/retry", ct);

    [HttpPost("training/datasets/{datasetId}/synthesize")]
    public Task<IActionResult> SynthesizeDataset(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/synthesize", ct);

    [HttpPost("training/datasets/{datasetId}/vectors/rebuild")]
    public Task<IActionResult> RebuildDatasetVectors(string datasetId, CancellationToken ct) =>
        ForwardAsync(HttpMethod.Post, $"api/training/datasets/{Uri.EscapeDataString(datasetId)}/vectors/rebuild", ct);

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

            // 音频生成命中参考头透传（可观测命中音色/情绪参考）。
            if (response.Headers.TryGetValues("X-Speaker-Audio-Key", out var speakerKeys))
            {
                Response.Headers["X-Speaker-Audio-Key"] = speakerKeys.ToArray();
            }

            if (response.Headers.TryGetValues("X-Emo-Audio-Key", out var emoKeys))
            {
                Response.Headers["X-Emo-Audio-Key"] = emoKeys.ToArray();
            }

            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }
}
