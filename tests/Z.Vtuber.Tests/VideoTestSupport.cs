using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

/// <summary>视频下载测试共用桩：B 站取流客户端、Cookie 客户端、HttpClient 工厂与消息处理器。</summary>
internal static class VideoTestSupport
{
    internal sealed class StubBiliApi : IBiliApiClient
    {
        public Func<string, string, string, int, CancellationToken, Task<VideoPlayInfo>>? OnGetVideoPlayInfo { get; set; }

        public string? LastBvid { get; private set; }
        public int LastPage { get; private set; }

        public Task<VideoPlayInfo> GetVideoPlayInfoAsync(string cookieId, string cookie, string bvid, int page, CancellationToken ct = default)
        {
            LastBvid = bvid;
            LastPage = page;
            if (OnGetVideoPlayInfo is null)
            {
                throw new NotSupportedException();
            }

            return OnGetVideoPlayInfo(cookieId, cookie, bvid, page, ct);
        }

        public Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookieId, string cookie, long roomId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NavInfo> GetNavInfoV2Async(string cookieId, string cookie, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookieId, string cookie, long mid, string offset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GuardInfo> GetRoomGuardListAsync(string cookieId, string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default) => throw new NotSupportedException();
    }

    internal sealed class StubCookieClient : ICookieServiceClient
    {
        public Func<CookieCheckoutResult> OnCheckout { get; set; } = () => new CookieCheckoutResult { CookieId = "cid", Cookie = "cookie" };

        public Task<CookieCheckoutResult> CheckoutAsync(CancellationToken ct = default) => Task.FromResult(OnCheckout());

        public Task<ReportInvalidResult> ReportInvalidAsync(string cookieId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CooldownAsync(string cookieId, TimeSpan duration, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WeiboCookieCheckoutResult> CheckoutWeiboAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReportInvalidResult> ReportWeiboInvalidAsync(string cookieId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CooldownWeiboAsync(string cookieId, TimeSpan duration, CancellationToken ct = default) => throw new NotSupportedException();
    }

    internal sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    internal sealed class StubHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Requests { get; } = [];
        public List<string?> RangeHeaders { get; } = [];
        public List<string?> IfRangeHeaders { get; } = [];
        public List<string?> Referers { get; } = [];
        public List<string?> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            RangeHeaders.Add(GetHeader(request, "Range"));
            IfRangeHeaders.Add(GetHeader(request, "If-Range"));
            Referers.Add(request.Headers.Referrer?.ToString());
            UserAgents.Add(GetHeader(request, "User-Agent"));
            return Task.FromResult(Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static string? GetHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }

    internal static VideoStreamService CreateService(
        IBiliApiClient bili,
        ICookieServiceClient cookieClient,
        HttpClient streamClient) =>
        new(
            bili,
            cookieClient,
            new StubHttpClientFactory(streamClient),
            new BilibiliCrawlerOptions(),
            NullLogger<VideoStreamService>.Instance);

    internal static VideoPlayInfo DashInfo(
        string title = "标题",
        string videoUrl = "https://cdn.example/video.m4v",
        string audioUrl = "https://cdn.example/audio.m4a",
        int pageCount = 1)
        => new()
        {
            Title = title,
            Cid = 111,
            PageCount = pageCount,
            VideoStreams = [new VideoStreamCandidate { Url = videoUrl, QualityId = 80, Bandwidth = 1000 }],
            AudioStreams = [new VideoStreamCandidate { Url = audioUrl, QualityId = 30280, Bandwidth = 200 }],
            IsFlvFallback = false,
        };

    internal static VideoPlayInfo FlvInfo(string title = "标题", string url = "https://cdn.example/video.flv")
        => new()
        {
            Title = title,
            Cid = 222,
            PageCount = 1,
            VideoStreams = [new VideoStreamCandidate { Url = url }],
            AudioStreams = [],
            IsFlvFallback = true,
        };

    internal static HttpResponseMessage BinaryResponse(byte[] payload, string contentType = "video/mp4")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }
}
