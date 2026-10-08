using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

public sealed class VideoDownloadApiTests
{
    private const string Bvid = "BV1xx411c7mD";

    private static VideoDownloadController CreateController(
        VideoTestSupport.StubBiliApi bili,
        HttpClient streamClient)
    {
        var service = VideoTestSupport.CreateService(bili, new VideoTestSupport.StubCookieClient(), streamClient);
        var controller = new VideoDownloadController(service);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static VideoTestSupport.StubBiliApi ApiReturning(VideoPlayInfo info) => new()
    {
        OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(info),
    };

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string ErrorMessage(ObjectResult result) =>
        result.Value!.GetType().GetProperty("error")!.GetValue(result.Value)!.ToString()!;

    [Fact]
    public async Task Download_DashVideo_ReturnsM4vWithDisposition()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload, "video/mp4") };
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo("标题", "https://cdn/video.m4v")), new HttpClient(handler));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(200, controller.Response.StatusCode);
        Assert.Equal("video/mp4", controller.Response.ContentType);
        Assert.Contains(".p01.video.m4v", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(payload, await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task Download_DashAudio_ReturnsM4aWithDisposition()
    {
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse([9, 9], "audio/mp4") };
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo("标题", audioUrl: "https://cdn/audio.m4a")), new HttpClient(handler));

        var result = await controller.Download(Bvid, "audio", 1, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("audio/mp4", controller.Response.ContentType);
        Assert.Contains(".p01.audio.m4a", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("/audio.m4a", handler.Requests[0].Split('?')[0]);
    }

    [Fact]
    public async Task Download_FlvFallbackVideo_ReturnsFlv()
    {
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse([1], "video/x-flv") };
        var controller = CreateController(ApiReturning(VideoTestSupport.FlvInfo()), new HttpClient(handler));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("video/x-flv", controller.Response.ContentType);
        Assert.Contains(".p01.video.flv", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Download_FlvFallbackAudio_Returns400()
    {
        var controller = CreateController(ApiReturning(VideoTestSupport.FlvInfo()), new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var result = await controller.Download(Bvid, "audio", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, status.StatusCode);
        Assert.Contains("无独立音频流", ErrorMessage(status));
    }

    [Fact]
    public async Task Download_InvalidBvid_Returns400WithoutUpstreamCall()
    {
        var handler = new VideoTestSupport.StubHttpHandler();
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo()), new HttpClient(handler));

        var result = await controller.Download("bad", "video", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, status.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_InvalidKind_Returns400()
    {
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo()), new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var result = await controller.Download(Bvid, "both", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, status.StatusCode);
    }

    [Fact]
    public async Task Download_PageOutOfRange_Returns400WithPageCount()
    {
        var api = new VideoTestSupport.StubBiliApi
        {
            OnGetVideoPlayInfo = (_, _, _, page, _) => page > 3
                ? Task.FromException<VideoPlayInfo>(new Z.Vtuber.Crawler.Crawlers.VideoPageOutOfRangeException(3))
                : Task.FromResult(VideoTestSupport.DashInfo()),
        };
        var controller = CreateController(api, new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var result = await controller.Download(Bvid, "video", 9, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, status.StatusCode);
        Assert.Contains("共 3 P", ErrorMessage(status));
    }

    [Fact]
    public async Task Download_ViewNotFound_Returns404()
    {
        var api = new VideoTestSupport.StubBiliApi
        {
            OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromException<VideoPlayInfo>(new Z.Vtuber.Crawler.Crawlers.VideoNotFoundException()),
        };
        var controller = CreateController(api, new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, status.StatusCode);
        Assert.Equal("视频不存在或不可访问", ErrorMessage(status));
    }

    [Fact]
    public async Task Download_NoCookie_Returns502()
    {
        var service = VideoTestSupport.CreateService(
            ApiReturning(VideoTestSupport.DashInfo()),
            new VideoTestSupport.StubCookieClient { OnCheckout = () => throw new Z.Vtuber.Common.Contracts.NoAvailableCookieException() },
            new HttpClient(new VideoTestSupport.StubHttpHandler()));
        var controller = new VideoDownloadController(service);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        Assert.Contains("暂无可用 B 站账号", ErrorMessage(status));
    }

    [Fact]
    public async Task Download_RiskControl_Returns502()
    {
        var api = new VideoTestSupport.StubBiliApi
        {
            OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromException<VideoPlayInfo>(new Z.Vtuber.Crawler.Crawlers.RiskControlException("risk", "target")),
        };
        var controller = CreateController(api, new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        Assert.Equal("B 站风控受限，请稍后重试", ErrorMessage(status));
    }

    [Fact]
    public async Task Download_FetchNetworkFailure_Returns502Not500()
    {
        var api = new VideoTestSupport.StubBiliApi
        {
            OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromException<VideoPlayInfo>(new HttpRequestException("connection refused")),
        };
        var controller = CreateController(api, new HttpClient(new VideoTestSupport.StubHttpHandler()));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        Assert.Equal("取流失败：上游服务不可用", ErrorMessage(status));
    }

    [Fact]
    public async Task Download_Upstream206_PassthroughHeadersAndForwardsRange()
    {
        var fragment = new byte[] { 1, 2, 3 };
        var handler = new VideoTestSupport.StubHttpHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(fragment) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 2, 300);
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            },
        };
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo()), new HttpClient(handler));
        controller.HttpContext.Request.Headers.Range = "bytes=0-2";

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(206, controller.Response.StatusCode);
        Assert.Equal("bytes 0-2/300", controller.Response.Headers.ContentRange.ToString());
        Assert.Equal(fragment.Length, controller.Response.ContentLength);
        Assert.Equal("bytes=0-2", handler.RangeHeaders[0]);
        Assert.Equal(fragment, await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task Download_Upstream416_PassthroughContentRange()
    {
        var handler = new VideoTestSupport.StubHttpHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(300);
                return response;
            },
        };
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo()), new HttpClient(handler));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(416, controller.Response.StatusCode);
        Assert.Equal("bytes */300", controller.Response.Headers.ContentRange.ToString());
    }

    [Fact]
    public void CrawlerVideoSource_DoesNotWriteToDisk()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var videoDir = Path.Combine(repoRoot!, "servers", "crawler", "Video");
        var offenders = Directory.EnumerateFiles(videoDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (File: file, Line: line, Number: index + 1)))
            .Where(entry => DiskWritePatterns.Any(pattern => entry.Line.Contains(pattern, StringComparison.Ordinal)))
            .Select(entry => $"{entry.File}:{entry.Number}: {entry.Line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Download_FullFlow_CreatesNoNewFilesInWorkingOrTempDirectory()
    {
        var workingBefore = Snapshot(Directory.GetCurrentDirectory());
        var tempBefore = Snapshot(Path.GetTempPath());

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };
        var controller = CreateController(ApiReturning(VideoTestSupport.DashInfo("标题")), new HttpClient(handler));

        var result = await controller.Download(Bvid, "video", 1, CancellationToken.None);
        Assert.IsType<EmptyResult>(result);
        Assert.Equal(payload, await ReadBodyAsync(controller.HttpContext));

        // 口径 11：不落盘——下载前后工作目录与临时目录均不得出现新增文件
        Assert.Equal(workingBefore, Snapshot(Directory.GetCurrentDirectory()));
        Assert.Empty(Snapshot(Path.GetTempPath()).Except(tempBefore));
    }

    private static string[] Snapshot(string directory) =>
        Directory.GetFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.Ordinal).ToArray();

    private static readonly string[] DiskWritePatterns =
    [
        "new FileStream",
        "File.WriteAll",
        "File.AppendAll",
        "File.Create",
        "File.Open",
        "StreamWriter",
        "BinaryWriter",
    ];

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "servers", "crawler", "Video")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
