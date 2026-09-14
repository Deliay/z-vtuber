using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Weibo;

namespace Z.Vtuber.Tests;

public sealed class WeiboApiClientTests
{
    private static readonly WeiboCrawlerOptions Options = new();

    private static WeiboApiClient CreateClient(
        RecordingHandler handler,
        IWeiboCookieSource? source = null) => new(
        source ?? new FakeWeiboCookieSource { Cookie = "SUB=a; SUBP=b" },
        Options,
        NullLogger<WeiboApiClient>.Instance,
        handler: handler);

    [Fact]
    public async Task GetUserTimeline_SetsHeadersAndContainerId()
    {
        var handler = new RecordingHandler(JsonResponse("""{"ok":1,"data":{"cards":[]}}"""));
        var client = CreateClient(handler);

        await client.GetUserTimelineAsync(12345);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("containerid=10760312345", req.RequestUri!.ToString());
        Assert.Equal("https://m.weibo.cn/u/12345", req.Headers.Referrer?.ToString());
        Assert.Contains("iPhone", req.Headers.UserAgent.ToString());
        Assert.True(req.Headers.TryGetValues("Cookie", out var cookieValues));
        Assert.Equal(["SUB=a; SUBP=b"], cookieValues.ToArray());
    }

    [Fact]
    public async Task GetUserTimeline_PassesSinceIdAsQuery()
    {
        var handler = new RecordingHandler(JsonResponse("""{"ok":1,"data":{"cards":[]}}"""));
        var client = CreateClient(handler);

        await client.GetUserTimelineAsync(12345, "s-abc");

        var req = Assert.Single(handler.Requests);
        Assert.Contains("since_id=s-abc", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetUserTimeline_ExtractsCards()
    {
        var body = """{"ok":1,"data":{"cards":[{"card_type":9,"mblog":{"id":"m1","created_at":"Sat Mar 04 12:30:45 +0800 2023"}}]}}""";
        var handler = new RecordingHandler(JsonResponse(body));
        var client = CreateClient(handler);

        var page = await client.GetUserTimelineAsync(12345);

        var item = Assert.Single(page.Items);
        Assert.Equal("m1", item.MblogId);
        Assert.Equal(1677904245, item.PubTs);
    }

    [Fact]
    public async Task GetUserTimeline_RiskResponse_ThrowsRiskControlException()
    {
        var handler = new RecordingHandler(JsonResponse("""{"ok":0,"msg":"cookie error"}"""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<WeiboRiskControlException>(() => client.GetUserTimelineAsync(12345));
    }

    [Fact]
    public async Task GetUserTimeline_RiskResponse_CarriesCookieIdentity()
    {
        var handler = new RecordingHandler(JsonResponse("""{"ok":0,"msg":"cookie error"}"""));
        var client = CreateClient(handler, new FakeWeiboCookieSource
        {
            Cookie = "SUB=login",
            IsLoginCookie = true,
            CookieId = "weibo-cookie-1",
        });

        var ex = await Assert.ThrowsAsync<WeiboRiskControlException>(() => client.GetUserTimelineAsync(12345));

        Assert.True(ex.IsLoginCookie);
        Assert.Equal("weibo-cookie-1", ex.CookieId);
    }

    [Fact]
    public async Task GetUserTimeline_AnonymousRiskResponse_CarriesNoCookieId()
    {
        var handler = new RecordingHandler(JsonResponse("""{"ok":0,"msg":"cookie error"}"""));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<WeiboRiskControlException>(() => client.GetUserTimelineAsync(12345));

        Assert.False(ex.IsLoginCookie);
        Assert.Null(ex.CookieId);
    }

    [Fact]
    public async Task GetUserTimeline_HtmlRiskBody_ReturnsEmptyPage()
    {
        var handler = new RecordingHandler(TextResponse("<html>captcha</html>", "text/html"));
        var client = CreateClient(handler);

        var page = await client.GetUserTimelineAsync(12345);

        Assert.Empty(page.Items);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage TextResponse(string text, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, contentType),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler(HttpResponseMessage response)
            : this(_ => response) { }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
