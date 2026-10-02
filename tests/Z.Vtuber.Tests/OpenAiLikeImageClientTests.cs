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
/// <c>POST {endpoint}/images/edits</c> multipart（model/prompt/图片，单图字段名 <c>image</c> 零回归、
/// 多图字段名 <c>image[]</c> 重复，带原文件名与 Content-Type）、
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

    private static IReadOnlyList<GenerateImageInput> Images(params GenerateImageInput[] images) => images;

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

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

        var result = await client.GenerateImageAsync(Provider(), Model(), "一只猫", Images(Image()));

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
        // 单图零回归：字段名恰为 image（非 image[]），文件名保持原值。
        Assert.Contains("name=image", body);
        Assert.Contains("filename=ref.png", body);
        Assert.DoesNotContain("image[]", body);
        Assert.Equal(1, CountOccurrences(body, "filename="));
    }

    [Fact]
    public async Task Generate_MultipleImages_UsesImageArrayFieldInOrder()
    {
        var payload = Convert.ToBase64String([9, 8, 7]);
        var handler = new StubHandler
        {
            Handler = _ => Json($"{{\"data\":[{{\"b64_json\":\"{payload}\"}}]}}"),
        };
        var client = Create(handler);

        var images = new[]
        {
            new GenerateImageInput([1, 1], "image/png", "a.png"),
            new GenerateImageInput([2, 2], "image/jpeg", ""),
            new GenerateImageInput([3, 3], "image/webp", "c.webp"),
        };

        var result = await client.GenerateImageAsync(Provider(), Model(), "p", images);

        Assert.Equal([9, 8, 7], result.Bytes);
        var body = handler.Bodies[0];
        Assert.Contains("name=model", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("\"image[]\"", body);
        Assert.Equal(3, CountOccurrences(body, "image[]"));
        Assert.Contains("filename=a.png", body);
        Assert.Contains("filename=image-2", body); // 文件名为空 → image-{序号}（1 基）
        Assert.Contains("filename=c.webp", body);
        Assert.Contains("Content-Type: image/png", body);
        Assert.Contains("Content-Type: image/jpeg", body);
        Assert.Contains("Content-Type: image/webp", body);
    }

    [Fact]
    public async Task Generate_NonSuccess_ThrowsHttpRequestException()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var client = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Images(Image())));
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
            () => client.GenerateImageAsync(Provider(), Model(), "p", Images(Image())));
    }

    [Fact]
    public async Task Generate_TransportFailure_PropagatesHttpRequestException()
    {
        var handler = new StubHandler { Handler = _ => throw new HttpRequestException("boom") };
        var client = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Images(Image())));
    }

    [Fact]
    public async Task Generate_Timeout_PropagatesTaskCanceled()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var client = Create(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GenerateImageAsync(Provider(), Model(), "p", Images(Image())));
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
