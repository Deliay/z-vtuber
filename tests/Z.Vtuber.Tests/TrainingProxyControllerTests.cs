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

    // ---- 数据集工具优化：14 条新镜像路由的方法 + 上游路径断言 ----

    [Fact]
    public async Task ListProviders_ForwardsGet()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListProviders(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/providers", handler.Requests[0]);
    }

    [Fact]
    public async Task CreateProvider_ForwardsPostBodyAndContentType()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.Created, """{"id":"p1","name":"n","apiKeySet":true}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"name":"n","type":"openai","endpoint":"https://x","apiKey":"k"}""");

        var result = await controller.CreateProvider(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/providers", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Contains("apiKey", handler.Bodies[0]);
    }

    [Fact]
    public async Task UpdateProvider_ForwardsPutBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"id":"p1"}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"name":"renamed"}""");

        var result = await controller.UpdateProvider("p1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal("/api/training/providers/p1", handler.Requests[0]);
        Assert.Equal("application/json", handler.ContentTypes[0]);
    }

    [Fact]
    public async Task DeleteProvider_ForwardsDelete()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DeleteProvider("p1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("DELETE", handler.Methods[0]);
        Assert.Equal("/api/training/providers/p1", handler.Requests[0]);
    }

    [Fact]
    public async Task DiscoverProviderModels_ForwardsGet()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"models":[{"model":"m"}]}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DiscoverProviderModels("p1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/providers/p1/models/discover", handler.Requests[0]);
    }

    [Fact]
    public async Task ImportProviderModels_ForwardsPostBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"imported":1,"skipped":0}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"models":["m"]}""");

        var result = await controller.ImportProviderModels("p1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/providers/p1/models/import", handler.Requests[0]);
        Assert.Contains("m", handler.Bodies[0]);
    }

    [Fact]
    public async Task ListModels_ForwardsGetWithQuery()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?enabled=true");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListModels(true, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/models?enabled=true", handler.Requests[0]);
    }

    [Fact]
    public async Task CreateModel_ForwardsPostBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.Created, """{"id":"m1"}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"providerId":"p1","model":"gpt"}""");

        var result = await controller.CreateModel(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/models", handler.Requests[0]);
        Assert.Contains("gpt", handler.Bodies[0]);
    }

    [Fact]
    public async Task UpdateModel_ForwardsPutBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"id":"m1"}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"enabled":false}""");

        var result = await controller.UpdateModel("m1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal("/api/training/models/m1", handler.Requests[0]);
    }

    [Fact]
    public async Task DeleteModel_ForwardsDelete()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DeleteModel("m1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("DELETE", handler.Methods[0]);
        Assert.Equal("/api/training/models/m1", handler.Requests[0]);
    }

    [Fact]
    public async Task RecognizeItem_ForwardsPostBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"text":"hi"}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"modelId":"m1"}""");

        var result = await controller.RecognizeItem("ds1", "it1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/items/it1/recognize", handler.Requests[0]);
        Assert.Contains("m1", handler.Bodies[0]);
    }

    [Fact]
    public async Task CreateEmotionTasks_ForwardsPostBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"created":2,"skipped":0}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"itemIds":["a","b"],"modelId":"m1"}""");

        var result = await controller.CreateEmotionTasks("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/items/emotion-tasks", handler.Requests[0]);
        Assert.Contains("itemIds", handler.Bodies[0]);
    }

    [Fact]
    public async Task ListEmotionTasks_ForwardsGetWithQuery()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"items":[],"counts":{}}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.QueryString = new QueryString("?datasetId=ds1&status=done&limit=10");
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListEmotionTasks("ds1", "done", 10, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/emotion-tasks?datasetId=ds1&status=done&limit=10", handler.Requests[0]);
    }

    [Fact]
    public async Task RetryEmotionTask_ForwardsPost()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"id":"t1","status":"pending"}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.RetryEmotionTask("t1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/emotion-tasks/t1/retry", handler.Requests[0]);
    }

    [Fact]
    public async Task CreateProvider_Upstream201_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.Created, """{"id":"p1","apiKeySet":true}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"name":"n"}""");

        var result = await controller.CreateProvider(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status201Created, controller.Response.StatusCode);
        Assert.Contains("apiKeySet", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task CreateEmotionTasks_Upstream404_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.NotFound, """{"error":"model not found"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"itemIds":["a"],"modelId":"m1"}""");

        var result = await controller.CreateEmotionTasks("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
        Assert.Contains("model not found", await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task RecognizeItem_Unreachable_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"modelId":"m1"}""");

        var result = await controller.RecognizeItem("ds1", "it1", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal(
            """{"error":"training service unavailable"}""",
            JsonSerializer.Serialize(status.Value));
    }

    // ---- 功能点模型解析：4 条新镜像路由（含 slug 转义与状态透传）----

    [Fact]
    public async Task ListFeaturePoints_ForwardsGet()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"items":[]}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ListFeaturePoints(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("GET", handler.Methods[0]);
        Assert.Equal("/api/training/feature-points", handler.Requests[0]);
    }

    [Fact]
    public async Task ResolveFeaturePointModel_ForwardsPostBody_WithEscapedSlug()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"slug":"a/b","source":"none"}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"inputCapabilities":["text"],"outputCapabilities":["text"]}""");

        var result = await controller.ResolveFeaturePointModel("a/b", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/feature-points/a%2Fb/model/resolve", handler.Requests[0]);
        Assert.Contains("inputCapabilities", handler.Bodies[0]);
    }

    [Fact]
    public async Task SetFeaturePointModel_ForwardsPutBody()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"slug":"fp"}""") };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Put, """{"modelId":"m1"}""");

        var result = await controller.SetFeaturePointModel("fp", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal("/api/training/feature-points/fp/model", handler.Requests[0]);
        Assert.Contains("m1", handler.Bodies[0]);
    }

    [Fact]
    public async Task ClearFeaturePointModel_ForwardsDelete()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, """{"slug":"fp"}""") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ClearFeaturePointModel("fp", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("DELETE", handler.Methods[0]);
        Assert.Equal("/api/training/feature-points/fp/model", handler.Requests[0]);
        Assert.Null(handler.ContentTypes[0]);
    }

    [Fact]
    public async Task ResolveFeaturePointModel_Upstream400_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.BadRequest, """{"error":"unsupported capability"}"""),
        };
        var controller = CreateController(new HttpClient(handler));
        PrepareBody(controller, HttpMethods.Post, """{"inputCapabilities":["bogus"],"outputCapabilities":[]}""");

        var result = await controller.ResolveFeaturePointModel("fp", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, controller.Response.StatusCode);
        Assert.Contains("unsupported capability", await ReadBodyAsync(controller.HttpContext));
    }

    // ---- 绘图接口镜像：multipart 请求体（含 boundary）与 image/png 二进制响应透传 ----

    private static async Task<byte[]> ReadBytesAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static HttpResponseMessage Png(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", "image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task GenerateTrainingImage_ForwardsPostMultipartBodyAndContentType()
    {
        var handler = new StubHandler { Handler = _ => Png([9, 9, 9]) };
        var controller = CreateController(new HttpClient(handler));
        const string boundary = "----boundary123";
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        var multipart = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"text\"\r\n\r\n一只猫\r\n--{boundary}--\r\n");
        controller.HttpContext.Request.Body = new MemoryStream(multipart);
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.GenerateTrainingImage(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/images/generate", handler.Requests[0]);
        Assert.NotNull(handler.ContentTypes[0]);
        Assert.StartsWith("multipart/form-data", handler.ContentTypes[0]);
        Assert.Contains(boundary, handler.ContentTypes[0]);
        Assert.Contains("一只猫", handler.Bodies[0]);
    }

    [Fact]
    public async Task GenerateTrainingImage_ForwardsMultipleSameNameImageParts()
    {
        var handler = new StubHandler { Handler = _ => Png([9, 9, 9]) };
        var controller = CreateController(new HttpClient(handler));
        const string boundary = "----boundary456";
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        var multipart = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"text\"\r\n\r\n两只猫\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"a.png\"\r\n" +
            "Content-Type: image/png\r\n\r\nAAA\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"b.png\"\r\n" +
            "Content-Type: image/png\r\n\r\nBBB\r\n" +
            $"--{boundary}--\r\n");
        controller.HttpContext.Request.Body = new MemoryStream(multipart);
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.GenerateTrainingImage(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/images/generate", handler.Requests[0]);
        Assert.NotNull(handler.ContentTypes[0]);
        Assert.StartsWith("multipart/form-data", handler.ContentTypes[0]);
        Assert.Contains(boundary, handler.ContentTypes[0]);
        var forwarded = handler.Bodies[0];
        Assert.Contains("两只猫", forwarded);
        Assert.Contains("filename=\"a.png\"", forwarded);
        Assert.Contains("filename=\"b.png\"", forwarded);
        Assert.Equal(2, forwarded.Split("name=\"image\"").Length - 1);
    }

    [Fact]
    public async Task GenerateTrainingImage_UpstreamImageResponse_Passthrough()
    {
        var handler = new StubHandler { Handler = _ => Png([7, 8, 9]) };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.ContentType = "multipart/form-data; boundary=b";
        controller.HttpContext.Request.Body = new MemoryStream([1]);
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.GenerateTrainingImage(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("image/png", controller.Response.ContentType);
        Assert.Equal([7, 8, 9], await ReadBytesAsync(controller.HttpContext));
    }

    [Fact]
    public async Task GenerateTrainingImage_Unreachable_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.ContentType = "multipart/form-data; boundary=b";
        controller.HttpContext.Request.Body = new MemoryStream([1]);

        var result = await controller.GenerateTrainingImage(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal(
            """{"error":"training service unavailable"}""",
            JsonSerializer.Serialize(status.Value));
    }
}
