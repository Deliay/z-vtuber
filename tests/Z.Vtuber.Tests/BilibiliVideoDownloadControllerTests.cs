using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

public sealed class BilibiliVideoDownloadControllerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Requests { get; } = [];
        public List<string?> RangeHeaders { get; } = [];
        public List<string?> IfRangeHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            RangeHeaders.Add(GetHeader(request, "Range"));
            IfRangeHeaders.Add(GetHeader(request, "If-Range"));
            return Task.FromResult(Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static string? GetHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static BilibiliVideoDownloadController CreateController(HttpClient client)
    {
        client.BaseAddress ??= new Uri("http://crawler");
        var controller = new BilibiliVideoDownloadController(new StubHttpClientFactory(client));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Download_StreamsContentAndPassthroughHeaders()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = "标题.p01.video.m4v",
                };
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            },
        };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("video/mp4", controller.Response.ContentType);
        Assert.Contains(".p01.video.m4v", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(payload, await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task Download_ForwardsRangeIfRangeAndQueryString()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) } };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.Headers.Range = "bytes=0-99";
        controller.HttpContext.Request.Headers.IfRange = "\"etag\"";
        controller.HttpContext.Request.QueryString = new QueryString("?kind=video&page=2");

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("/api/video-download/BV1xx411c7mD?kind=video&page=2", handler.Requests[0]);
        Assert.Equal("bytes=0-99", handler.RangeHeaders[0]);
        Assert.Equal("\"etag\"", handler.IfRangeHeaders[0]);
    }

    [Fact]
    public async Task Download_WithoutRange_DoesNotForwardRangeHeader()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) } };
        var controller = CreateController(new HttpClient(handler));

        await controller.Download("BV1xx411c7mD", CancellationToken.None);

        Assert.Null(handler.RangeHeaders[0]);
        Assert.Null(handler.IfRangeHeaders[0]);
    }

    [Fact]
    public async Task Download_Upstream206_PassthroughPartialContent()
    {
        var fragment = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(fragment) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 4, 200);
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            },
        };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status206PartialContent, controller.Response.StatusCode);
        Assert.Equal("bytes 0-4/200", controller.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(fragment, await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task Download_Upstream416_PassthroughContentRangeWithout502()
    {
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(200);
                return response;
            },
        };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, controller.Response.StatusCode);
        Assert.Equal("bytes */200", controller.Response.Headers.ContentRange.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(HttpStatusCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(HttpStatusCode.BadGateway, StatusCodes.Status502BadGateway)]
    public async Task Download_UpstreamErrorBody_Passthrough(HttpStatusCode upstream, int expected)
    {
        const string body = """{"error":"视频不存在或不可访问"}""";
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(upstream)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
        };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(expected, content.StatusCode);
        Assert.Equal(body, content.Content);
        Assert.Equal("application/json", content.ContentType);
    }

    [Fact]
    public async Task Download_UpstreamUnreachable_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Fact]
    public async Task Download_UpstreamServerError_Returns502()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.Download("BV1xx411c7mD", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }
}
