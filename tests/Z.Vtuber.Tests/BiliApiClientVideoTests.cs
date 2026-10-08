using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Z.Vtuber.Crawler.Crawlers;

namespace Z.Vtuber.Tests;

public sealed class BiliApiClientVideoTests
{
    private const string ViewUrl = "https://api.bilibili.com/x/web-interface/view";
    private const string PlayUrlUrl = "https://api.bilibili.com/x/player/wbi/playurl";
    private const string Bvid = "BV1xx411c7mD";

    private static string NavJson() => """
    {"code":0,"data":{"mid":1,"uname":"u","wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png","sub_url":"https://i0.hdslb.com/bfs/wbi/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.png"}}}
    """;

    private static string ViewJson(int pageCount = 1)
    {
        var pages = string.Join(",", Enumerable.Range(1, pageCount)
            .Select(i => $"{{\"cid\":{100 + i},\"page\":{i},\"part\":\"p{i}\"}}"));
        return $"{{\"code\":0,\"data\":{{\"title\":\"标题\",\"pages\":[{pages}]}}}}";
    }

    private static string DashJson() => """
    {"code":0,"data":{"dash":{"video":[{"id":32,"baseUrl":"https://cdn/low.m4v","bandwidth":100},{"id":80,"baseUrl":"https://cdn/high.m4v","bandwidth":200}],"audio":[{"id":30280,"baseUrl":"https://cdn/a.m4a","bandwidth":50}]}}}
    """;

    private static string FlvJson() => """
    {"code":0,"data":{"durl":[{"order":1,"url":"https://cdn/v.flv"}]}}
    """;

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(route(request));
        }
    }

    private sealed class StubAccountProvider(IServiceProvider provider) : IBilibiliAccountProvider
    {
        public Task<IServiceProvider> GetProviderAsync(string cookieId, string cookie, CancellationToken ct = default) =>
            Task.FromResult(provider);
    }

    private static async Task<(IBilibiliAccountProvider Provider, RoutingHandler Handler)> BuildProviderAsync(
        Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var handler = new RoutingHandler(route);
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient(handler));
        services.AddSingleton(new CookieJar("cookie"));
        services.AddBilibiliCrawlers(addHttpClient: false);
        var provider = services.BuildServiceProvider();
        var account = provider.GetRequiredService<BilibiliAccount>();
        await account.InitializeAsync(CancellationToken.None);
        return (new StubAccountProvider(provider), handler);
    }

    private static HttpResponseMessage Route(HttpRequestMessage request, string playUrlJson)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/x/web-interface/nav")
        {
            return Json(NavJson());
        }

        if (path == "/x/web-interface/view")
        {
            return Json(ViewJson());
        }

        if (path == "/x/player/wbi/playurl")
        {
            return Json(playUrlJson);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVideoPlayInfoAsync_ParsesDashAndSignsPlayUrlRequest()
    {
        var (provider, handler) = await BuildProviderAsync(r => Route(r, DashJson()));
        var client = new BiliApiClient(provider);

        var info = await client.GetVideoPlayInfoAsync("cid", "cookie", Bvid, 1, CancellationToken.None);

        Assert.Equal("标题", info.Title);
        Assert.Equal(101, info.Cid);
        Assert.Equal(1, info.PageCount);
        Assert.False(info.IsFlvFallback);
        Assert.Equal(2, info.VideoStreams.Count);
        Assert.Single(info.AudioStreams);
        Assert.Equal("https://cdn/high.m4v", info.VideoStreams.Single(v => v.QualityId == 80).Url);

        var viewRequest = handler.Requests.Single(r => r.Contains("/x/web-interface/view"));
        Assert.Contains($"bvid={Bvid}", viewRequest);

        var playUrlRequest = handler.Requests.Single(r => r.Contains("/x/player/wbi/playurl"));
        Assert.Contains("w_rid=", playUrlRequest);
        Assert.Contains("wts=", playUrlRequest);
        Assert.Contains($"bvid={Bvid}", playUrlRequest);
        Assert.Contains("fnval=16", playUrlRequest);
    }

    [Fact]
    public async Task GetVideoPlayInfoAsync_FallsBackToFlvWhenDashMissing()
    {
        var (provider, handler) = await BuildProviderAsync(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/player/wbi/playurl")
            {
                // fnval=16 无 DASH/durl → 客户端以 fnval=0 回退
                return Json(request.RequestUri.Query.Contains("fnval=0") ? FlvJson() : """{"code":0,"data":{}}""");
            }

            return Route(request, FlvJson());
        });
        var client = new BiliApiClient(provider);

        var info = await client.GetVideoPlayInfoAsync("cid", "cookie", Bvid, 1, CancellationToken.None);

        Assert.True(info.IsFlvFallback);
        Assert.Equal("https://cdn/v.flv", Assert.Single(info.VideoStreams).Url);
        Assert.Equal(2, handler.Requests.Count(r => r.Contains("/x/player/wbi/playurl")));
    }

    [Fact]
    public async Task GetVideoPlayInfoAsync_PlayUrlNonZero_ThrowsWithCode()
    {
        var (provider, _) = await BuildProviderAsync(r => Route(r, """{"code":-403,"message":"forbidden"}"""));
        var client = new BiliApiClient(provider);

        var ex = await Assert.ThrowsAsync<VideoPlayUrlException>(
            () => client.GetVideoPlayInfoAsync("cid", "cookie", Bvid, 1, CancellationToken.None));

        Assert.Equal(-403, ex.Code);
    }

    [Fact]
    public async Task GetVideoPlayInfoAsync_ViewNotFound_Throws()
    {
        var (provider, _) = await BuildProviderAsync(request =>
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
                ? Json(NavJson())
                : Json("""{"code":-404,"message":"not found"}"""));
        var client = new BiliApiClient(provider);

        await Assert.ThrowsAsync<VideoNotFoundException>(
            () => client.GetVideoPlayInfoAsync("cid", "cookie", Bvid, 1, CancellationToken.None));
    }

    [Fact]
    public void ParseView_OutOfRange_ThrowsWithPageCount()
    {
        using var doc = JsonDocument.Parse(ViewJson(pageCount: 3));

        var ex = Assert.Throws<VideoPageOutOfRangeException>(() => BiliVideoResponseParser.ParseView(doc.RootElement, 4));

        Assert.Equal(3, ex.PageCount);
    }

    [Fact]
    public void ParseView_NotFound_Throws()
    {
        using var doc = JsonDocument.Parse("""{"code":-404}""");

        Assert.Throws<VideoNotFoundException>(() => BiliVideoResponseParser.ParseView(doc.RootElement, 1));
    }

    [Fact]
    public void TryParsePlayUrl_Dash_UsesBaseUrlAndBaseUnderscoreUrl()
    {
        using var doc = JsonDocument.Parse("""
        {"code":0,"data":{"dash":{"video":[{"id":80,"base_url":"https://cdn/v.m4v","bandwidth":1}],"audio":[{"id":30280,"baseUrl":"https://cdn/a.m4a","bandwidth":1}]}}}
        """);

        Assert.True(BiliVideoResponseParser.TryParsePlayUrl(doc.RootElement, out var parsed));
        Assert.NotNull(parsed);
        Assert.False(parsed!.FlvFallback);
        Assert.Equal("https://cdn/v.m4v", Assert.Single(parsed.Video).Url);
        Assert.Equal("https://cdn/a.m4a", Assert.Single(parsed.Audio).Url);
    }

    [Fact]
    public void TryParsePlayUrl_Durl_MarksFlvFallback()
    {
        using var doc = JsonDocument.Parse(FlvJson());

        Assert.True(BiliVideoResponseParser.TryParsePlayUrl(doc.RootElement, out var parsed));
        Assert.NotNull(parsed);
        Assert.True(parsed!.FlvFallback);
        Assert.Equal("https://cdn/v.flv", Assert.Single(parsed.Video).Url);
    }

    [Fact]
    public void TryParsePlayUrl_NonZeroCode_Throws()
    {
        using var doc = JsonDocument.Parse("""{"code":-403}""");

        var ex = Assert.Throws<VideoPlayUrlException>(() => BiliVideoResponseParser.TryParsePlayUrl(doc.RootElement, out _));
        Assert.Equal(-403, ex.Code);
    }

    [Fact]
    public void TryParsePlayUrl_NoData_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""{"code":0,"data":{}}""");

        Assert.False(BiliVideoResponseParser.TryParsePlayUrl(doc.RootElement, out _));
    }
}
