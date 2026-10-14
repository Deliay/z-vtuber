using System.Net;
using System.Net.Http.Headers;
using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

public sealed class VideoCacheServiceTests
{
    private const string Bvid = "BV1xx411c7mD";

    private static VideoStreamService Service(
        IVideoObjectCache cache,
        VideoTestSupport.StubHttpHandler handler,
        VideoTestSupport.StubBiliApi? bili = null,
        VideoTestSupport.StubCookieClient? cookie = null)
        => VideoTestSupport.CreateService(
            bili ?? new VideoTestSupport.StubBiliApi
            {
                OnGetVideoPlayInfo = (_, _, _, _, _) => Task.FromResult(VideoTestSupport.DashInfo()),
            },
            cookie ?? new VideoTestSupport.StubCookieClient(),
            new HttpClient(handler),
            cache);

    private static async Task<byte[]> ReadAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task PrepareAsync_CacheHit_ReturnsCachedContentWithoutUpstreamOrCookie()
    {
        var payload = new byte[] { 9, 8, 7 };
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => new VideoCacheRead
            {
                StatusCode = 200,
                Content = new MemoryStream(payload),
                ContentType = "video/mp4",
                ContentDisposition = "attachment; filename*=UTF-8''标题.p01.video.m4v",
                ContentLength = payload.Length,
            },
        };
        var handler = new VideoTestSupport.StubHttpHandler();
        var bili = new VideoTestSupport.StubBiliApi
        {
            OnGetVideoPlayInfo = (_, _, _, _, _) => throw new NotSupportedException("must not fetch"),
        };
        var cookie = new VideoTestSupport.StubCookieClient { OnCheckout = () => throw new NotSupportedException("must not checkout") };

        await using var result = await Service(cache, handler, bili, cookie)
            .PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("video/mp4", result.ContentType);
        Assert.Contains("标题", result.ContentDisposition);
        Assert.Equal(payload.Length, result.ContentLength);
        Assert.Equal(payload, await ReadAsync(result.Content!));
        Assert.Empty(handler.Requests);
        Assert.Equal(VideoCacheKey.For(Bvid, 1, VideoStreamKind.Video), cache.Reads[0].Key);
    }

    [Fact]
    public async Task PrepareAsync_CacheHit_ForwardsRangeAndIfRangeToCache()
    {
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => new VideoCacheRead
            {
                StatusCode = 206,
                Content = new MemoryStream([1, 2]),
                ContentType = "video/mp4",
                ContentRange = "bytes 0-1/100",
                ContentLength = 2,
            },
        };

        await using var result = await Service(cache, new VideoTestSupport.StubHttpHandler())
            .PrepareAsync(Bvid, "video", 1, "bytes=0-1", "\"etag\"", CancellationToken.None);

        Assert.Equal(206, result.StatusCode);
        Assert.Equal("bytes 0-1/100", result.ContentRange);
        Assert.Equal("bytes=0-1", cache.Reads[0].Range);
        Assert.Equal("\"etag\"", cache.Reads[0].IfRange);
    }

    [Fact]
    public async Task PrepareAsync_CacheHit416_PassesThroughContentRangeWithoutContent()
    {
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => new VideoCacheRead
            {
                StatusCode = 416,
                ContentType = "video/mp4",
                ContentRange = "bytes */100",
            },
        };

        await using var result = await Service(cache, new VideoTestSupport.StubHttpHandler())
            .PrepareAsync(Bvid, "video", 1, "bytes=999-", null, CancellationToken.None);

        Assert.Equal(416, result.StatusCode);
        Assert.Equal("bytes */100", result.ContentRange);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task PrepareAsync_CacheMiss_TeesUpstreamIntoCacheOnFullRead()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var upload = new VideoTestSupport.FakeVideoCacheUpload();
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => null,
            OnBeginUpload = (_, _) => upload,
        };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };

        await using var result = await Service(cache, handler)
            .PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        Assert.Equal(payload, await ReadAsync(result.Content!));
        Assert.True(upload.Completed);
        Assert.False(upload.Aborted);
        Assert.Equal(payload, upload.Bytes);
        Assert.Single(cache.Uploads);
        Assert.Equal(VideoCacheKey.For(Bvid, 1, VideoStreamKind.Video), cache.Uploads[0].Key);
        Assert.Equal("video/mp4", cache.Uploads[0].Metadata.ContentType);
        Assert.Contains(".p01.video.m4v", cache.Uploads[0].Metadata.ContentDisposition);
    }

    [Fact]
    public async Task PrepareAsync_CacheMissWithRange_DoesNotWriteCache()
    {
        var upload = new VideoTestSupport.FakeVideoCacheUpload();
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => null,
            OnBeginUpload = (_, _) => upload,
        };
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

        await using var result = await Service(cache, handler)
            .PrepareAsync(Bvid, "video", 1, "bytes=0-1", null, CancellationToken.None);

        Assert.Equal(206, result.StatusCode);
        Assert.Equal(new byte[] { 1, 2 }, await ReadAsync(result.Content!));
        Assert.Empty(cache.Uploads);
        Assert.False(upload.Completed);
    }

    [Fact]
    public async Task PrepareAsync_CacheReadThrows_FallsBackToUpstream()
    {
        var payload = new byte[] { 4, 5, 6 };
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => throw new IOException("s3 down"),
            OnBeginUpload = (_, _) => new VideoTestSupport.FakeVideoCacheUpload(),
        };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };

        await using var result = await Service(cache, handler)
            .PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(payload, await ReadAsync(result.Content!));
    }

    [Fact]
    public async Task PrepareAsync_BeginUploadThrows_ServesWithoutCache()
    {
        var payload = new byte[] { 7, 7 };
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => null,
            OnBeginUpload = (_, _) => throw new IOException("s3 down"),
        };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };

        await using var result = await Service(cache, handler)
            .PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(payload, await ReadAsync(result.Content!));
    }

    [Fact]
    public async Task PrepareAsync_UploadWriteFails_StillServesFullContentAndAborts()
    {
        var payload = new byte[] { 1, 2, 3 };
        var upload = new VideoTestSupport.FakeVideoCacheUpload { FailWrites = true };
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => null,
            OnBeginUpload = (_, _) => upload,
        };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };

        await using var result = await Service(cache, handler)
            .PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        Assert.Equal(payload, await ReadAsync(result.Content!));
        Assert.True(upload.Aborted);
        Assert.False(upload.Completed);
    }

    [Fact]
    public async Task PrepareAsync_DisposedBeforeEof_AbortsUpload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var upload = new VideoTestSupport.FakeVideoCacheUpload();
        var cache = new VideoTestSupport.FakeVideoObjectCache
        {
            OnRead = (_, _, _) => null,
            OnBeginUpload = (_, _) => upload,
        };
        var handler = new VideoTestSupport.StubHttpHandler { Handler = _ => VideoTestSupport.BinaryResponse(payload) };

        var result = await Service(cache, handler)
            .PrepareAsync(Bvid, "video", 1, null, null, CancellationToken.None);

        var buffer = new byte[2];
        await result.Content!.ReadExactlyAsync(buffer, CancellationToken.None);
        await result.DisposeAsync();

        Assert.True(upload.Aborted);
        Assert.False(upload.Completed);
    }

    [Fact]
    public async Task CachingReadStream_ReadToEof_CompletesUpload()
    {
        var upload = new VideoTestSupport.FakeVideoCacheUpload();
        await using var stream = new CachingReadStream(new MemoryStream([1, 2, 3]), upload, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var bytes = await ReadAsync(stream);

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.True(upload.Completed);
        Assert.Equal(new byte[] { 1, 2, 3 }, upload.Bytes);
    }

    [Fact]
    public async Task CachingReadStream_DisposeWithoutEof_AbortsUpload()
    {
        var upload = new VideoTestSupport.FakeVideoCacheUpload();
        var stream = new CachingReadStream(new MemoryStream([1, 2, 3, 4]), upload, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var buffer = new byte[2];
        await stream.ReadExactlyAsync(buffer, CancellationToken.None);
        await stream.DisposeAsync();

        Assert.True(upload.Aborted);
        Assert.False(upload.Completed);
    }
}
