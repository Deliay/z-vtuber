using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

public class RecordingTranscriptProxyControllerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Requests { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string?> ContentTypes { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method.Method);
            ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());

            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static RecordingTranscriptProxyController CreateController(HttpClient client)
    {
        client.BaseAddress ??= new Uri("http://transcript");
        var controller = new RecordingTranscriptProxyController(new StubHttpClientFactory(client));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task ListRecordings_ProxiesToTranscriptService()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListRecordings(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("/api/recordings", handler.Requests[0]);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.StartsWith("application/json", controller.Response.ContentType);
        Assert.Contains("\"items\"", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task GetRoomTranscripts_BuildsUpstreamPath()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, "{}") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        await controller.GetRoomTranscripts(123, CancellationToken.None);

        Assert.Equal("/api/recordings/123/transcripts", handler.Requests[0]);
    }

    [Fact]
    public async Task ListTranscriptTasks_ForwardsQueryString()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, "{}") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?roomId=42&status=FAILED");
        controller.HttpContext.Response.Body = new MemoryStream();

        await controller.ListTranscriptTasks(CancellationToken.None);

        Assert.Equal("/api/transcripts/task?roomId=42&status=FAILED", handler.Requests[0]);
    }

    [Fact]
    public async Task Enqueue_ForwardsPostMethodAndBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"taskId":"x","status":"PENDING"}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.ContentType = "application/json";
        controller.HttpContext.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes("""{"recordingId":"a","modelId":"b"}"""));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.EnqueueTranscript(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/transcripts/enqueue", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("recordingId", handler.Bodies[0]);
    }

    [Fact]
    public async Task Unreachable_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.ListRecordings(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Fact]
    public async Task UpstreamNotFound_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.NotFound, """{"error":"task not found"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.GetTranscriptTask("missing", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
        Assert.Contains("task not found", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task Sse_ForwardsEventStreamAndDisablesBuffering()
    {
        var payload = "data: {\"start\":1,\"end\":2,\"text\":\"hi\"}\n\nevent: done\ndata: {\"status\":\"DONE\"}\n\n";
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return response;
            },
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.StreamTranscriptSegments("abc", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("/api/transcripts/task/abc/sse", handler.Requests[0]);
        Assert.Equal("text/event-stream", controller.Response.ContentType);
        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", controller.Response.Headers["X-Accel-Buffering"].ToString());
        Assert.Contains("event: done", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task Retry_ForwardsPostWithoutBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, "{}") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Response.Body = new MemoryStream();

        await controller.RetryTranscriptTask("abc", CancellationToken.None);

        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/transcripts/task/abc/retry", handler.Requests[0]);
    }

    [Fact]
    public async Task UpdateModel_ForwardsPutMethod()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, "{}") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.Method = HttpMethods.Put;
        controller.HttpContext.Request.ContentType = "application/json";
        controller.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"enabled":false}"""));
        controller.HttpContext.Response.Body = new MemoryStream();

        await controller.UpdateTranscriptModel("m1", CancellationToken.None);

        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal("/api/transcripts/models/m1", handler.Requests[0]);
    }

    [Fact]
    public async Task CandidateRooms_ProxiesToTranscriptService()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListTranscriptCandidateRooms(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/transcripts/candidate-rooms", handler.Requests[0]);
    }

    [Fact]
    public async Task DeleteSettings_ForwardsDeleteAndPassesThrough204()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DeleteRoomTranscriptSettings(321, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("DELETE", handler.Methods[0]);
        Assert.Equal("/api/recordings/321/transcripts/settings", handler.Requests[0]);
        Assert.Equal(StatusCodes.Status204NoContent, controller.Response.StatusCode);
    }

    [Fact]
    public async Task DeleteSettings_PassesThrough404()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.NotFound, """{"error":"transcript settings not found"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DeleteRoomTranscriptSettings(322, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
        Assert.Contains("transcript settings not found", await ReadBodyAsync(controller.HttpContext));
    }
}
