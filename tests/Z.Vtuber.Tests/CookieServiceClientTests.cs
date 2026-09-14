using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public sealed class CookieServiceClientTests
{
    private static CookieServiceClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://cookie-service") },
            NullLogger<CookieServiceClient>.Instance);

    [Fact]
    public async Task CheckoutWeiboAsync_200_MapsCookieFields()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"cookieId":"c1","accountId":"a1","cookie":"SUB=x"}"""));
        var client = CreateClient(handler);

        var result = await client.CheckoutWeiboAsync();

        Assert.True(result.Available);
        Assert.Equal("c1", result.CookieId);
        Assert.Equal("a1", result.AccountId);
        Assert.Equal("SUB=x", result.Cookie);
        Assert.Equal("/api/cookie/weibo/checkout", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
    }

    [Fact]
    public async Task CheckoutWeiboAsync_404_ReturnsUnavailable_NoThrow()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{"reason":"no_available_cookie"}"""));
        var client = CreateClient(handler);

        var result = await client.CheckoutWeiboAsync();

        Assert.False(result.Available);
    }

    [Fact]
    public async Task CheckoutWeiboAsync_200_EmptyCookie_ReturnsUnavailable()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"cookieId":"c1","accountId":"a1"}"""));
        var client = CreateClient(handler);

        var result = await client.CheckoutWeiboAsync();

        Assert.False(result.Available);
    }

    [Fact]
    public async Task ReportWeiboInvalidAsync_200_ReturnsRefreshed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"cookieId":"c1","accountId":"a1","cookie":"SUB=new","refreshed":true}"""));
        var client = CreateClient(handler);

        var result = await client.ReportWeiboInvalidAsync("c1");

        Assert.Equal(ReportInvalidStatus.Refreshed, result.Status);
        Assert.Equal("c1", result.CookieId);
        Assert.Equal("SUB=new", result.Cookie);
        Assert.Equal("/api/cookie/weibo/report-invalid", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReportWeiboInvalidAsync_410_ReturnsSessionInvalidWithAccountId()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Gone, """{"reason":"session_invalid","accountId":"a1"}"""));
        var client = CreateClient(handler);

        var result = await client.ReportWeiboInvalidAsync("c1");

        Assert.Equal(ReportInvalidStatus.SessionInvalid, result.Status);
        Assert.Equal("a1", result.AccountId);
    }

    [Fact]
    public async Task ReportWeiboInvalidAsync_404_ReturnsNotFound()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        var client = CreateClient(handler);

        var result = await client.ReportWeiboInvalidAsync("c1");

        Assert.Equal(ReportInvalidStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ReportWeiboInvalidAsync_504_ReturnsTimeout()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.GatewayTimeout, "{}"));
        var client = CreateClient(handler);

        var result = await client.ReportWeiboInvalidAsync("c1");

        Assert.Equal(ReportInvalidStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task CooldownWeiboAsync_PostsPathAndBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        await client.CooldownWeiboAsync("c1", TimeSpan.FromMinutes(5));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/cookie/weibo/cooldown", request.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"cookieId\":\"c1\"", handler.Bodies[0]);
        Assert.Contains("\"durationSeconds\":300", handler.Bodies[0]);
    }

    [Fact]
    public async Task CheckoutAsync_404_StillThrowsNoAvailableCookie()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{"reason":"no_available_cookie"}"""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<NoAvailableCookieException>(() => client.CheckoutAsync());
        Assert.Equal("/api/cookie/checkout", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReportInvalidAsync_UsesBilibiliPath()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"cookieId":"b1","accountId":"a1","cookie":"SESSDATA=new","refreshed":true}"""));
        var client = CreateClient(handler);

        var result = await client.ReportInvalidAsync("b1");

        Assert.Equal(ReportInvalidStatus.Refreshed, result.Status);
        Assert.Equal("/api/cookie/report-invalid", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CooldownAsync_UsesBilibiliPath()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        await client.CooldownAsync("b1", TimeSpan.FromSeconds(60));

        Assert.Equal("/api/cookie/cooldown", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            }
            else
            {
                Bodies.Add(string.Empty);
            }

            return responder(request);
        }
    }
}
