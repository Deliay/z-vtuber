using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Z.Vtuber.Common.Contracts;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

public sealed class VideoStreamServiceTests
{
    private const string Bvid = "BV1xx411c7mD";

    [Theory]
    [InlineData("BV1xx411c7mD", true)]
    [InlineData("BV1xx411c7m", false)]
    [InlineData("bv1xx411c7mD", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidBvid_ChecksShape(string? bvid, bool expected)
        => Assert.Equal(expected, VideoStreamService.IsValidBvid(bvid));

    [Theory]
    [InlineData("video", true, VideoStreamKind.Video)]
    [InlineData("audio", true, VideoStreamKind.Audio)]
    [InlineData("Video", false, VideoStreamKind.Video)]
    [InlineData("both", false, VideoStreamKind.Video)]
    [InlineData(null, false, VideoStreamKind.Video)]
    public void TryParseKind_AcceptsOnlyLowercaseVideoAndAudio(string? kind, bool expected, VideoStreamKind _)
    {
        Assert.Equal(expected, VideoStreamService.TryParseKind(kind, out _));
    }

    [Fact]
    public void MapPlayUrlError_ReturnsReadableReasonForPermissionCode()
    {
        Assert.Equal("取流失败：权限受限或该视频不可播放", VideoStreamService.MapPlayUrlError(-403));
        Assert.Equal("取流失败：权限受限或该视频不可播放", VideoStreamService.MapPlayUrlError(-1));
    }

    [Fact]
    public void Select_DashVideo_PicksHighestQualityThenBandwidth()
    {
        var info = new VideoPlayInfo
        {
            VideoStreams =
            [
                new VideoStreamCandidate { Url = "low", QualityId = 32, Bandwidth = 9999 },
                new VideoStreamCandidate { Url = "high-a", QualityId = 80, Bandwidth = 1000 },
                new VideoStreamCandidate { Url = "high-b", QualityId = 80, Bandwidth = 2000 },
            ],
            AudioStreams = [new VideoStreamCandidate { Url = "a", QualityId = 30280 }],
        };

        var selected = VideoStreamService.Select(info, VideoStreamKind.Video);

        Assert.NotNull(selected);
        Assert.Equal("high-b", selected!.Url);
        Assert.Equal("video/mp4", selected.ContentType);
        Assert.Equal("m4v", selected.Extension);
    }

    [Fact]
    public void Select_DashAudio_PicksHighestAudioQuality()
    {
        var info = new VideoPlayInfo
        {
            VideoStreams = [new VideoStreamCandidate { Url = "v", QualityId = 80 }],
            AudioStreams = [new VideoStreamCandidate { Url = "a1", QualityId = 30216 }, new VideoStreamCandidate { Url = "a2", QualityId = 30280 }],
        };

        var selected = VideoStreamService.Select(info, VideoStreamKind.Audio);

        Assert.NotNull(selected);
        Assert.Equal("a2", selected!.Url);
        Assert.Equal("audio/mp4", selected.ContentType);
        Assert.Equal("m4a", selected.Extension);
    }

    [Fact]
    public void Select_FlvFallback_VideoReturnsFlv()
    {
        var selected = VideoStreamService.Select(VideoTestSupport.FlvInfo(url: "flv-url"), VideoStreamKind.Video);

        Assert.NotNull(selected);
        Assert.Equal("flv-url", selected!.Url);
        Assert.Equal("video/x-flv", selected.ContentType);
        Assert.Equal("flv", selected.Extension);
    }

    [Fact]
    public void Select_FlvFallback_AudioReturnsNull()
        => Assert.Null(VideoStreamService.Select(VideoTestSupport.FlvInfo(), VideoStreamKind.Audio));

    [Fact]
    public async Task PrepareAsync_DashVideo_StreamsUpstreamAndBuildsHeaders()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi
            {
                OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo("标题", "https://cdn.example/video.m4v")),
            },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler));

        using var result = await service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("video/mp4", result.ContentType);
        Assert.Contains(".p01.video.m4v", result.ContentDisposition);
        Assert.Equal("bytes", result.AcceptRanges);
        Assert.Equal(payload.Length, result.ContentLength);
        Assert.Equal(payload, await ReadAsync(result.Content!));
        Assert.Equal("https://www.bilibili.com/", handler.Referers[0]);
    }

    [Fact]
    public async Task PrepareAsync_ForwardsRangeAndIfRangeAndPassthrough206()
    {
        var handler = new VideoTestSupport.StubHttpHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2]) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 100);
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            },
        };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo()) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler));

        using var result = await service.PrepareAsync(Bvid, "video", 1, "bytes=0-1", "\"etag\"", CancellationToken.None);

        Assert.Equal(206, result.StatusCode);
        Assert.Equal("bytes=0-1", handler.RangeHeaders[0]);
        Assert.Equal("\"etag\"", handler.IfRangeHeaders[0]);
        Assert.Equal("bytes 0-1/100", result.ContentRange);
    }

    [Fact]
    public async Task PrepareAsync_Upstream416_PassthroughWithoutContent()
    {
        var handler = new VideoTestSupport.StubHttpHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(100);
                return response;
            },
        };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo()) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler));

        using var result = await service.PrepareAsync(Bvid, "video", 1, "bytes=999-", null, CancellationToken.None);

        Assert.Equal(416, result.StatusCode);
        Assert.Equal("bytes */100", result.ContentRange);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(null, "video", 1, "bvid")]
    [InlineData("bad", "video", 1, "bvid")]
    [InlineData(Bvid, "both", 1, "kind")]
    [InlineData(Bvid, null, 1, "kind")]
    [InlineData(Bvid, "video", 0, "page")]
    public async Task PrepareAsync_InvalidArguments_Return400(string? bvid, string? kind, int page, string _)
    {
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi(),
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(bvid, kind, page, null, null, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task PrepareAsync_NoAvailableCookie_Returns502()
    {
        var cookie = new VideoTestSupport.StubCookieClient { OnCheckout = () => throw new NoAvailableCookieException() };
        var service = VideoTestSupport.CreateService(new VideoTestSupport.StubBiliApi(), cookie, new HttpClient());

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("暂无可用 B 站账号", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_VideoNotFound_Returns404()
    {
        var service = VideoTestSupport.CreateService(ThrowingApi(new VideoNotFoundException()), new VideoTestSupport.StubCookieClient(), new HttpClient());

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("视频不存在或不可访问", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_PageOutOfRange_Returns400WithPageCount()
    {
        var service = VideoTestSupport.CreateService(ThrowingApi(new VideoPageOutOfRangeException(3)), new VideoTestSupport.StubCookieClient(), new HttpClient());

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 5, null, null, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("共 3 P", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_PlayUrlPermissionDenied_Returns502()
    {
        var service = VideoTestSupport.CreateService(ThrowingApi(new VideoPlayUrlException(-403)), new VideoTestSupport.StubCookieClient(), new HttpClient());

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "audio", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("取流失败：权限受限或该视频不可播放", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_RiskControl_Returns502()
    {
        var service = VideoTestSupport.CreateService(ThrowingApi(new RiskControlException("risk", "target")), new VideoTestSupport.StubCookieClient(), new HttpClient());

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("B 站风控受限，请稍后重试", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_FlvFallbackAudio_Returns400()
    {
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.FlvInfo()) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "audio", 1, null, null, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("无独立音频流", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_UpstreamUnreachable_Returns502()
    {
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => throw new HttpRequestException("boom") };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo()) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("取流失败：上游服务不可用", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_UpstreamServerError_Returns502()
    {
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo()) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task PrepareAsync_FetchNetworkFailure_Returns502()
    {
        var service = VideoTestSupport.CreateService(
            ThrowingApi(new HttpRequestException("connection refused")),
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("取流失败：上游服务不可用", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_FetchTimeout_Returns502()
    {
        // 取数 HttpClient 默认超时抛 TaskCanceledException；非客户端取消须映射 502 而非静默 500
        var service = VideoTestSupport.CreateService(
            ThrowingApi(new TaskCanceledException("timeout")),
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("取流失败：上游服务不可用", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_FetchNonJson_Returns502()
    {
        var service = VideoTestSupport.CreateService(
            ThrowingApi(new JsonException("unexpected token")),
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("取流失败：上游服务不可用", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_DashAudioMissing_Returns400()
    {
        var info = new VideoPlayInfo
        {
            Title = "t",
            Cid = 1,
            PageCount = 1,
            VideoStreams = [new VideoStreamCandidate { Url = "https://cdn/v.m4v", QualityId = 80 }],
            AudioStreams = [],
            IsFlvFallback = false,
        };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(info) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<VideoDownloadException>(
            () => service.PrepareAsync(Bvid, "audio", 1, null, null, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("无独立音频流", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_ClientCancelled_CancelsUpstreamRequestWithoutResidual()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse([1, 2, 3]) };
        var service = VideoTestSupport.CreateService(
            new VideoTestSupport.StubBiliApi { OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo()) },
            new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler));

        // 客户端已取消：RequestAborted 联动取消，上游 SendAsync 观测到已取消 token 并抛出
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PrepareAsync(Bvid, "video", 1, null, null, cts.Token));

        var observed = Assert.Single(handler.CancellationTokens);
        Assert.True(observed.IsCancellationRequested);
    }

    private static VideoTestSupport.StubBiliApi ThrowingApi(Exception exception) => new()
    {
        OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromException<VideoPlayInfo>(exception),
    };

    private static async Task<byte[]> ReadAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
