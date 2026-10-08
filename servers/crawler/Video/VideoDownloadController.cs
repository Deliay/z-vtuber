using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Crawler.Video;

/// <summary>
/// crawler 服务对内视频取流端点：<c>GET /api/video-download/{bvid}?kind=&page=</c>。
/// 成功时回写状态码与 Content-* 头后逐段透传上游原始流（不落盘、不整段缓冲）；错误统一 <c>{ "error": "&lt;中文原因&gt;" }</c>。
/// </summary>
[ApiController]
[Route("api/video-download")]
public sealed class VideoDownloadController(VideoStreamService service) : ControllerBase
{
    [HttpGet("{bvid}")]
    public async Task<IActionResult> Download(
        string bvid,
        [FromQuery] string? kind,
        [FromQuery] int page = 1,
        CancellationToken ct = default)
    {
        var range = Request.Headers.TryGetValue("Range", out var rangeValues) ? rangeValues.ToString() : null;
        var ifRange = Request.Headers.TryGetValue("If-Range", out var ifRangeValues) ? ifRangeValues.ToString() : null;

        VideoDownloadResult result;
        try
        {
            result = await service.PrepareAsync(bvid, kind, page, range, ifRange, ct);
        }
        catch (VideoDownloadException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }

        using (result)
        {
            Response.StatusCode = result.StatusCode;
            Response.ContentType = result.ContentType;
            if (result.ContentDisposition is not null)
            {
                Response.Headers.ContentDisposition = result.ContentDisposition;
            }

            if (result.AcceptRanges is not null)
            {
                Response.Headers.AcceptRanges = result.AcceptRanges;
            }

            if (result.ContentRange is not null)
            {
                Response.Headers.ContentRange = result.ContentRange;
            }

            if (result.ContentLength is not null)
            {
                Response.ContentLength = result.ContentLength;
            }

            if (result.Content is not null)
            {
                await result.Content.CopyToAsync(Response.Body, ct);
            }

            return new EmptyResult();
        }
    }
}
