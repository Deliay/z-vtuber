using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

public class TrainingProxyControllerTests
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

    private static TrainingProxyController CreateController(HttpClient client)
    {
        client.BaseAddress ??= new Uri("http://training");
        var controller = new TrainingProxyController(new StubHttpClientFactory(client));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void PrepareBody(TrainingProxyController controller, string method, string body)
    {
        controller.HttpContext.Request.Method = method;
        controller.HttpContext.Request.ContentType = "application/json";
        controller.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.HttpContext.Response.Body = new MemoryStream();
    }

    // ---- Plan 1.7：8 条镜像路由的方法 + 上游路径断言 ----

    [Fact]
    public async Task ListDatasets_ForwardsGetWithQuery()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?type=audio");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListDatasets("audio", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/datasets?type=audio", handler.Requests[0]);
    }

    [Fact]
    public async Task CreateDataset_ForwardsPostBodyAndContentType()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.Created, """{"id":"ds1","name":"n","type":"audio"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"name":"n","type":"audio"}""");

        var result = await controller.CreateDataset(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("audio", handler.Bodies[0]);
    }

    [Fact]
    public async Task RenameDataset_ForwardsPutBodyAndContentType()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"id":"ds1","name":"renamed","type":"audio"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"name":"renamed"}""");

        var result = await controller.RenameDataset("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("renamed", handler.Bodies[0]);
    }

    [Fact]
    public async Task DeleteDataset_ForwardsDelete()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DeleteDataset("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("DELETE", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1", handler.Requests[0]);
        Assert.Null(handler.ContentTypes[0]);
    }

    [Fact]
    public async Task ListItems_ForwardsGetWithQuery()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString =
            new QueryString("?audioType=transcript&source=t-1");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListItems("ds1", "transcript", "t-1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/items?audioType=transcript&source=t-1", handler.Requests[0]);
    }

    [Fact]
    public async Task ImportItems_ForwardsPostBodyAndContentType()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"imported":3,"skipped":1}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"taskId":"t-1"}""");

        var result = await controller.ImportItems("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/items/import", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("t-1", handler.Bodies[0]);
    }

    [Fact]
    public async Task BatchDeleteItems_ForwardsPostBodyAndContentType()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"deleted":2}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"itemIds":["a","b"]}""");

        var result = await controller.BatchDeleteItems("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/items/batch-delete", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("itemIds", handler.Bodies[0]);
    }

    [Fact]
    public async Task UpdateItem_ForwardsPutBodyAndContentType()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"id":"it1","text":"hi"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"text":"hi"}""");

        var result = await controller.UpdateItem("ds1", "it1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/items/it1", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("hi", handler.Bodies[0]);
    }

    // ---- Plan 1.7：上游状态码与响应体透传 ----

    [Fact]
    public async Task ListDatasets_Upstream200_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"items":[{"id":"ds1"}]}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListDatasets("audio", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.StartsWith("application/json", controller.Response.ContentType);
        Assert.Contains("ds1", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task CreateDataset_Upstream201_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.Created, """{"id":"ds1","name":"n"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"name":"n","type":"audio"}""");

        var result = await controller.CreateDataset(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status201Created, controller.Response.StatusCode);
        Assert.Contains("ds1", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task DeleteDataset_Upstream204_Passthrough()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DeleteDataset("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status204NoContent, controller.Response.StatusCode);
    }

    [Fact]
    public async Task ListDatasets_Upstream400_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.BadRequest, """{"error":"unsupported dataset type"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?type=video");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListDatasets("video", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, controller.Response.StatusCode);
        Assert.Contains("unsupported dataset type", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task RenameDataset_Upstream404_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.NotFound, """{"error":"dataset not found"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"name":"renamed"}""");

        var result = await controller.RenameDataset("missing", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
        Assert.Contains("dataset not found", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task ImportItems_Upstream400_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.BadRequest, """{"error":"task not done"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"taskId":"t-1"}""");

        var result = await controller.ImportItems("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, controller.Response.StatusCode);
        Assert.Contains("task not done", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task ListItems_Upstream404_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.NotFound, """{"error":"dataset not found"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?audioType=transcript&source=t-1");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListItems("missing", "transcript", "t-1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("/api/training/datasets/missing/items?audioType=transcript&source=t-1", handler.Requests[0]);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
        Assert.Contains("dataset not found", await ReadBodyAsync(controller.HttpContext));
    }

    // ---- Plan 1.7：上游不可达 / 超时 → 502 ----

    [Fact]
    public async Task Unreachable_Returns502WithTrainingServiceUnavailable()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.ListDatasets("audio", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal(
            """{"error":"training service unavailable"}""",
            JsonSerializer.Serialize(status.Value));
    }

    [Fact]
    public async Task UpstreamTimeout_Returns502WithTrainingServiceUnavailable()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?audioType=transcript");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListItems("ds1", "transcript", null, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal(
            """{"error":"training service unavailable"}""",
            JsonSerializer.Serialize(status.Value));
    }

    [Fact]
    public async Task ImportItems_Unreachable_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"taskId":"t-1"}""");

        var result = await controller.ImportItems("ds1", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal(
            """{"error":"training service unavailable"}""",
            JsonSerializer.Serialize(status.Value));
    }

    [Fact]
    public async Task UpdateItem_UpstreamTimeout_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"text":"hi"}""");

        var result = await controller.UpdateItem("ds1", "it1", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal(
            """{"error":"training service unavailable"}""",
            JsonSerializer.Serialize(status.Value));
    }
}
