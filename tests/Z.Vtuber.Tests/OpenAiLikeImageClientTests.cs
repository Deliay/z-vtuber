using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>
/// OpenAI-like 图片客户端契约（假 <see cref="HttpMessageHandler"/>，不打外网）：
/// <c>POST {endpoint}/images/edits</c> multipart（model/prompt/image，带原文件名与 Content-Type）、
/// <c>data[0].b64_json</c> 解析、非 2xx/超时/传输失败与不可解析输出的异常映射；工厂按 provider.Type 派发。
/// </summary>
public class OpenAiLikeImageClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string?> Auths { get; } = [];
        public List<string?> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Auths.Add(request.Headers.Authorization?.ToString());
            ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static OpenAiLikeImageClient Create(StubHandler handler, Action<ImageOptions>? configure = null)
    {
        var options = new ImageOptions();
        configure?.Invoke(options);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://llm.test") };
        return new OpenAiLikeImageClient(http, options, NullLogger<OpenAiLikeImageClient>.Instance);
    }

    private static TrainingProviderRecord Provider() => new()
    {
        Name = "p", Type = TrainingProviderTypes.OpenAi, Endpoint = "http://llm.test/", ApiKey = "secret",
    };

    private static TrainingModelRecord Model() => new() { Model = "img-model" };

    private static GenerateImageInput Image() => new([1, 2, 3], "image/png", "ref.png");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Generate_Success_BuildsMultipartAndParsesB64()
    {
        var payload = Convert.ToBase64String([9, 8, 7]);
        var handler = new StubHandler
        {
            Handler = _ => Json($"{{\"data\":[{{\"b64_json\":\"{payload}\"}}]}}"),
        };
        var client = Create(handler);

        var result = await client.GenerateImageAsync(Provider(), Model(), "一只猫", Image());

        Assert.Equal([9, 8, 7], result.Bytes);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal("/images/edits", handler.Paths[0]);
        Assert.Equal("Bearer secret", handler.Auths[0]);
        Assert.Equal("multipart/form-data", handler.ContentTypes[0]);
        var body = handler.Bodies[0];
        Assert.Contains("name=model", body);
        Assert.Contains("img-model", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("一只猫", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("b64_json", body);
        Assert.Contains("name=image", body);
        Assert.Contains("filename=ref.png", body);
    }

    [Fact]
    public async Task Generate_NonSuccess_ThrowsHttpRequestException()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var client = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Image()));
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"url":"http://x/image.png"}]}""")]
    [InlineData("""{"data":[{"b64_json":"not-base64!!"}]}""")]
    [InlineData("""{"data":[{"b64_json":""}]}""")]
    [InlineData("""{}""")]
    public async Task Generate_UnparsableOutput_ThrowsModelOutputException(string body)
    {
        var handler = new StubHandler { Handler = _ => Json(body) };
        var client = Create(handler);

        await Assert.ThrowsAsync<ModelOutputException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Image()));
    }

    [Fact]
    public async Task Generate_TransportFailure_PropagatesHttpRequestException()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var client = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Image()));
    }

    [Fact]
    public async Task Generate_Timeout_PropagatesTaskCanceled()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var client = Create(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Image()));
    }

    [Fact]
    public void Factory_OpenAi_ReturnsOpenAiLikeClient()
    {
        var factory = new ImageModelClientFactory(
            new StubHttpClientFactory(new HttpClient(new StubHandler())),
            Options.Create(new ImageOptions()),
            NullLoggerFactory.Instance);

        Assert.IsType<OpenAiLikeImageClient>(factory.Create(TrainingProviderTypes.OpenAi));
    }

    [Theory]
    [InlineData(TrainingProviderTypes.Claude)]
    [InlineData("unknown")]
    public void Factory_NonOpenAi_ThrowsImageNotSupported(string providerType)
    {
        var factory = new ImageModelClientFactory(
            new StubHttpClientFactory(new HttpClient(new StubHandler())),
            Options.Create(new ImageOptions()),
            NullLoggerFactory.Instance);

        Assert.Throws<ModelImageNotSupportedException>(() => factory.Create(providerType));
    }
}
