using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Weibo;

namespace Z.Vtuber.Tests;

public sealed class WeiboVisitorCookieProviderTests
{
    private static WeiboVisitorCookieProvider CreateProvider(
        RecordingHandler handler,
        WeiboCrawlerOptions? options = null,
        TimeProvider? time = null) => new(
        options ?? new WeiboCrawlerOptions(),
        NullLogger<WeiboVisitorCookieProvider>.Instance,
        handler,
        time ?? new FakeTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    public async Task GetOrRefresh_OneStep_ReturnsCookieHeader()
    {
        var jsonp = """visitor_callback({"retcode":"20000000","data":{"sub":"AAA","subp":"BBB"}})""";
        var handler = new RecordingHandler(jsonp);
        var provider = CreateProvider(handler);

        var cookie = await provider.GetOrRefreshAsync();

        Assert.Equal("SUB=AAA; SUBP=BBB", cookie);
        Assert.Single(handler.Requests);
        Assert.Contains("visitor/genvisitor2", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetOrRefresh_IsCachedWithinRefreshInterval()
    {
        var handler = new RecordingHandler("""visitor_callback({"data":{"sub":"A","subp":"B"}})""");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = CreateProvider(handler, options: new WeiboCrawlerOptions { CookieRefreshIntervalMinutes = 30 }, time: time);

        await provider.GetOrRefreshAsync();
        time.Advance(TimeSpan.FromMinutes(10));
        var second = await provider.GetOrRefreshAsync();

        Assert.Equal("SUB=A; SUBP=B", second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetOrRefresh_RefreshesAfterInterval()
    {
        var handler = new RecordingHandler("""visitor_callback({"data":{"sub":"A","subp":"B"}})""");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = CreateProvider(handler, options: new WeiboCrawlerOptions { CookieRefreshIntervalMinutes = 30 }, time: time);

        await provider.GetOrRefreshAsync();
        time.Advance(TimeSpan.FromMinutes(40));
        await provider.GetOrRefreshAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Renew_ClearsAndReacquires()
    {
        var handler = new RecordingHandler("""visitor_callback({"data":{"sub":"A","subp":"B"}})""");
        var provider = CreateProvider(handler);

        await provider.GetOrRefreshAsync();
        await provider.RenewAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentCalls_AcquireOnlyOnce()
    {
        var handler = new RecordingHandler("""visitor_callback({"data":{"sub":"A","subp":"B"}})""");
        var provider = CreateProvider(handler);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => provider.GetOrRefreshAsync()));

        Assert.Equal(1, handler.Requests.Count);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/javascript"),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler(string body)
        {
            _responder = _ => JsonResponse(body);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request.RequestUri?.ToString() ?? string.Empty));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
