using System.Net;
using Z.Vtuber.Crawler.Crawlers;

namespace Z.Vtuber.Tests;

public class RiskControlHandlerTests
{
    private static HttpMessageInvoker CreateInvoker(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new RiskControlHandler(new StubInnerHandler(responder)));

    private static HttpRequestMessage CreateWebSocketHandshakeRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://broadcastlv.chat.bilibili.com/sub");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        return request;
    }

    [Fact]
    public async Task WebSocketUpgradeRequest_DoesNotBufferResponseContent()
    {
        // 回归测试：ReadAsStringAsync 会把响应内容缓冲成只读 MemoryStream，
        // 导致 ClientWebSocket 握手失败（The base stream is not writeable）
        var trackingContent = new TrackingContent();
        using var invoker = CreateInvoker(_ => new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
        {
            Content = trackingContent,
        });

        using var request = CreateWebSocketHandshakeRequest();
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.SwitchingProtocols, response.StatusCode);
        Assert.False(trackingContent.WasRead);
    }

    [Fact]
    public async Task WebSocketUpgradeRequest_With412Response_BypassesClassification()
    {
        // 握手阶段的风控由 ClientWebSocket 自身抛 WebSocketException，handler 不应介入
        using var invoker = CreateInvoker(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent("<html>安全风控</html>"),
        });

        using var request = CreateWebSocketHandshakeRequest();
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task NormalRequest_With412Response_ThrowsRiskControlException()
    {
        using var invoker = CreateInvoker(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent("<html></html>"),
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/space/wbi/acc/info");
        var exception = await Assert.ThrowsAsync<RiskControlException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.Equal(request.RequestUri!.ToString(), exception.Target);
    }

    [Fact]
    public async Task NormalRequest_WithRiskControlHtml_ThrowsRiskControlException()
    {
        using var invoker = CreateInvoker(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>安全风控</body></html>", System.Text.Encoding.UTF8, "text/html"),
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
        await Assert.ThrowsAsync<RiskControlException>(
            () => invoker.SendAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task NormalRequest_WithHealthyResponse_PassesThrough()
    {
        using var invoker = CreateInvoker(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":0,"message":"ok"}""", System.Text.Encoding.UTF8, "application/json"),
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"code\":0", await response.Content.ReadAsStringAsync());
    }

    private sealed class StubInnerHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }
}
