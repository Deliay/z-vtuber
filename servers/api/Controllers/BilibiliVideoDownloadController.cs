using Microsoft.AspNetCore.Mvc;

namespace Z.Vtuber.Controllers;

/// <summary>
/// 将 <c>/api/b/video/**</c> 流式代理至 crawler 服务的视频取流端点（named client <c>CrawlerService</c>）。
/// 参考 <see cref="BilibiliLiveRecordingController.DownloadRecording"/>：透传 <c>Range</c>/<c>If-Range</c> 请求头，
/// <c>ResponseHeadersRead</c> + <c>CopyToAsync</c> 逐段转发，透传 200/206 的 <c>Content-*</c> 与 416 的 <c>Content-Range</c>；
/// 上游 400/404/502 原样透传响应体（保留可读原因），上游不可达或超时返回 502 <c>{"error":"crawler service unavailable"}</c>。
/// </summary>
[ApiController]
[Route("api/b/video")]
public class BilibiliVideoDownloadController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    internal const string CrawlerClientName = "CrawlerService";

    [HttpGet("{bvid}/download")]
    public async Task<IActionResult> Download(string bvid, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(CrawlerClientName);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/video-download/{Uri.EscapeDataString(bvid)}{Request.QueryString}");

            // 透传 Range/If-Range：是否可满足（206/416）由上游判定，本层不解释
            if (Request.Headers.TryGetValue("Range", out var range) && range.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Range", range.ToArray());
            }

            if (Request.Headers.TryGetValue("If-Range", out var ifRange) && ifRange.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange.ToArray());
            }

            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "crawler service unavailable" });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上游超时（非客户端取消）也应返回契约约定的 502
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "crawler service unavailable" });
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 上游 416（区间越界）原样透传 Content-Range，避免被归并为 502
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                if (response.Content.Headers.ContentRange is not null)
                {
                    Response.Headers.ContentRange = response.Content.Headers.ContentRange.ToString();
                }

                return new EmptyResult();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest
                || response.StatusCode == System.Net.HttpStatusCode.NotFound
                || response.StatusCode == System.Net.HttpStatusCode.BadGateway)
            {
                // 上游 400/404/502 原样透传响应体与状态码，保留可读中文原因
                var json = await response.Content.ReadAsStringAsync(ct);
                var passthrough = Content(json, "application/json");
                passthrough.StatusCode = (int)response.StatusCode;
                return passthrough;
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "crawler service unavailable" });
            }

            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (response.Content.Headers.ContentDisposition is not null)
            {
                Response.Headers.ContentDisposition = response.Content.Headers.ContentDisposition.ToString();
            }

            if (response.Headers.AcceptRanges.Count > 0)
            {
                Response.Headers.AcceptRanges = response.Headers.AcceptRanges.ToString();
            }

            if (response.Content.Headers.ContentRange is not null)
            {
                Response.Headers.ContentRange = response.Content.Headers.ContentRange.ToString();
            }

            if (response.Content.Headers.ContentLength is not null)
            {
                Response.ContentLength = response.Content.Headers.ContentLength;
            }

            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }
}
