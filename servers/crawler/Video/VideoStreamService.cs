using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Contracts;

namespace Z.Vtuber.Crawler.Video;

/// <summary>
/// B 站视频取流编排：校验入参 → 领取 Cookie 取流信息 → 按下载模式筛选目标流 → 用独立 HttpClient
/// 以 <c>ResponseHeadersRead</c> 打开 B 站 CDN 原始流（Range/If-Range 透传），全程不落盘、不整段缓冲。
/// </summary>
public sealed class VideoStreamService
{
    /// <summary>CDN 流式传输专用 named HttpClient；<c>Program.cs</c> 中将其整体超时设为 Infinite。</summary>
    public const string StreamClientName = "VideoStream";

    private const string UpstreamReferer = "https://www.bilibili.com/";
    private static readonly Regex BvidPattern = new("^BV[0-9A-Za-z]{10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IBiliApiClient _bili;
    private readonly ICookieServiceClient _cookieServiceClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ILogger<VideoStreamService> _logger;

    public VideoStreamService(
        IBiliApiClient bili,
        ICookieServiceClient cookieServiceClient,
        IHttpClientFactory httpClientFactory,
        BilibiliCrawlerOptions options,
        ILogger<VideoStreamService> logger)
    {
        _bili = bili;
        _cookieServiceClient = cookieServiceClient;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<VideoDownloadResult> PrepareAsync(
        string? bvid,
        string? kind,
        int page,
        string? range,
        string? ifRange,
        CancellationToken ct)
    {
        if (!IsValidBvid(bvid))
        {
            throw new VideoDownloadException(400, "参数非法：bvid 格式不正确");
        }

        if (!TryParseKind(kind, out var streamKind))
        {
            throw new VideoDownloadException(400, "参数非法：kind 必须为 video 或 audio");
        }

        if (page < 1)
        {
            throw new VideoDownloadException(400, "参数非法：page 必须大于等于 1");
        }

        string cookieId;
        string cookie;
        try
        {
            var checkout = await _cookieServiceClient.CheckoutAsync(ct);
            cookieId = checkout.CookieId;
            cookie = checkout.Cookie;
        }
        catch (NoAvailableCookieException)
        {
            throw new VideoDownloadException(502, "暂无可用 B 站账号，无法取流");
        }

        VideoPlayInfo info;
        try
        {
            info = await _bili.GetVideoPlayInfoAsync(cookieId, cookie, bvid!, page, ct);
        }
        catch (VideoNotFoundException)
        {
            throw new VideoDownloadException(404, "视频不存在或不可访问");
        }
        catch (VideoPageOutOfRangeException ex)
        {
            throw new VideoDownloadException(400, $"分 P 越界：该视频共 {ex.PageCount} P");
        }
        catch (VideoPlayUrlException ex)
        {
            _logger.LogInformation("Video playurl failed for {Bvid} page {Page}: code {Code}", bvid, page, ex.Code);
            throw new VideoDownloadException(502, MapPlayUrlError(ex.Code));
        }
        catch (RiskControlException)
        {
            throw new VideoDownloadException(502, "B 站风控受限，请稍后重试");
        }
        catch (CrawlBackoffActiveException)
        {
            throw new VideoDownloadException(502, "B 站风控受限，请稍后重试");
        }
        catch (CookieSessionInvalidException)
        {
            throw new VideoDownloadException(502, "暂无可用 B 站账号，无法取流");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 非客户端取消（取数 HttpClient 超时）：按上游不可用映射 502，避免静默 500
            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }
        catch (HttpRequestException)
        {
            // B 站 API 返回非 2xx（未被 RiskControlClassifier 判为风控）或网络不可达
            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }
        catch (JsonException)
        {
            // 返回非 JSON（如 HTML 错误页）导致解析失败
            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }

        var selection = Select(info, streamKind);
        if (selection is null)
        {
            if (streamKind == VideoStreamKind.Audio)
            {
                throw new VideoDownloadException(400, "该视频无独立音频流（仅整段视频流），无法只下载音频");
            }

            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }

        return await OpenUpstreamAsync(info, selection, page, range, ifRange, ct);
    }

    /// <summary>校验收到的 bvid 是否形如 <c>^BV[0-9A-Za-z]{10}$</c>。</summary>
    public static bool IsValidBvid(string? bvid) => !string.IsNullOrEmpty(bvid) && BvidPattern.IsMatch(bvid);

    /// <summary>解析下载模式；仅接受大小写敏感的 <c>video</c> / <c>audio</c>。</summary>
    public static bool TryParseKind(string? kind, out VideoStreamKind streamKind)
    {
        switch (kind)
        {
            case "video":
                streamKind = VideoStreamKind.Video;
                return true;
            case "audio":
                streamKind = VideoStreamKind.Audio;
                return true;
            default:
                streamKind = default;
                return false;
        }
    }

    /// <summary>
    /// 按下载模式筛选目标流（纯函数）：DASH 下取对应数组清晰度/音质档位最高者（同档取码率更高者）；
    /// flv 回退时仅视频可取，音频返回 null 交由上层报 400。
    /// </summary>
    public static VideoStreamSelection? Select(VideoPlayInfo info, VideoStreamKind kind)
    {
        if (info.IsFlvFallback)
        {
            if (kind == VideoStreamKind.Audio)
            {
                return null;
            }

            var flvUrl = info.VideoStreams.Count > 0 ? info.VideoStreams[0].Url : null;
            return string.IsNullOrEmpty(flvUrl)
                ? null
                : new VideoStreamSelection(VideoStreamKind.Video, flvUrl, "video/x-flv", "flv");
        }

        var candidates = kind == VideoStreamKind.Video ? info.VideoStreams : info.AudioStreams;
        var best = candidates
            .OrderByDescending(c => c.QualityId)
            .ThenByDescending(c => c.Bandwidth)
            .FirstOrDefault();

        if (best is null || string.IsNullOrEmpty(best.Url))
        {
            return null;
        }

        return kind == VideoStreamKind.Video
            ? new VideoStreamSelection(VideoStreamKind.Video, best.Url, "video/mp4", "m4v")
            : new VideoStreamSelection(VideoStreamKind.Audio, best.Url, "audio/mp4", "m4a");
    }

    /// <summary>playurl 非 0 code（含 -403 权限受限）到可读中文原因的映射。</summary>
    public static string MapPlayUrlError(int code) => "取流失败：权限受限或该视频不可播放";

    private async Task<VideoDownloadResult> OpenUpstreamAsync(
        VideoPlayInfo info,
        VideoStreamSelection selection,
        int page,
        string? range,
        string? ifRange,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(StreamClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, selection.Url);
        // 独立流式通道：仅 Referer + 浏览器 UA，不挂 RiskControlHandler（其 ReadAsStringAsync 会整段缓冲并破坏二进制）
        request.Headers.Referrer = new Uri(UpstreamReferer);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.BrowserUserAgent);
        if (!string.IsNullOrEmpty(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (!string.IsNullOrEmpty(ifRange))
        {
            request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Open upstream video stream failed for {Url}", selection.Url);
            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }

        var status = (int)response.StatusCode;
        if (status == StatusCodes.Status416RangeNotSatisfiable)
        {
            var contentRange = response.Content.Headers.ContentRange?.ToString();
            response.Dispose();
            return new VideoDownloadResult
            {
                StatusCode = StatusCodes.Status416RangeNotSatisfiable,
                ContentType = selection.ContentType,
                ContentRange = contentRange,
            };
        }

        if (response.StatusCode != System.Net.HttpStatusCode.OK
            && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new VideoDownloadException(502, "取流失败：上游服务不可用");
        }

        var fileName = VideoDownloadFileName.Build(info.Title, page, KindName(selection.Kind), selection.Extension);
        var content = await response.Content.ReadAsStreamAsync(ct);

        return new VideoDownloadResult
        {
            StatusCode = status,
            ContentType = selection.ContentType,
            ContentDisposition = VideoDownloadFileName.FormatContentDisposition(fileName),
            AcceptRanges = response.Headers.AcceptRanges.Count > 0 ? response.Headers.AcceptRanges.ToString() : "bytes",
            ContentRange = response.Content.Headers.ContentRange?.ToString(),
            ContentLength = response.Content.Headers.ContentLength,
            Content = content,
            Upstream = response,
        };
    }

    private static string KindName(VideoStreamKind kind) => kind == VideoStreamKind.Video ? "video" : "audio";
}
