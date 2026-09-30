using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// api 代理新增镜像：<c>synthesize</c>（audio/wav 二进制 + X-* 命中头 + 状态码透传）与
/// <c>vectors/rebuild</c>；上游不可达 502 <c>training service unavailable</c>。
/// </summary>
public class TrainingSynthesizeProxyTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Requests { get; } = [];
        public List<string> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method.Method);
            return Task.FromResult(Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
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
        controller.HttpContext.Response.Body = new MemoryStream();
        controller.HttpContext.Request.ContentType = "application/json";
        controller.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"text":"你好"}"""));
        return controller;
    }

    private static HttpResponseMessage Wav(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "audio/wav");
        response.Headers.TryAddWithoutValidation("X-Speaker-Audio-Key", "speaker-key");
        return response;
    }

    [Fact]
    public async Task Synthesize_ForwardsPost_AndPassesThroughWavAndHeaders()
    {
        var handler = new StubHandler { Handler = _ => Wav([1, 2, 3]) };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.SynthesizeDataset("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/synthesize", handler.Requests[0]);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("audio/wav", controller.Response.ContentType);
        Assert.Equal("speaker-key", controller.Response.Headers["X-Speaker-Audio-Key"].ToString());
        controller.HttpContext.Response.Body.Position = 0;
        Assert.Equal([1, 2, 3], ((MemoryStream)controller.HttpContext.Response.Body).ToArray());
    }

    [Fact]
    public async Task Synthesize_Upstream502_Passthrough()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("""{"error":"tts service unavailable"}""", Encoding.UTF8, "application/json"),
            },
        };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.SynthesizeDataset("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, controller.Response.StatusCode);
    }

    [Fact]
    public async Task Synthesize_Unreachable_Returns502()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.SynthesizeDataset("ds1", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Equal("""{"error":"training service unavailable"}""", JsonSerializer.Serialize(status.Value));
    }

    [Fact]
    public async Task RebuildVectors_ForwardsPost()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"updated":3}""", Encoding.UTF8, "application/json"),
            },
        };
        var controller = CreateController(new HttpClient(handler));

        var result = await controller.RebuildDatasetVectors("ds1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal("/api/training/datasets/ds1/vectors/rebuild", handler.Requests[0]);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
    }
}
