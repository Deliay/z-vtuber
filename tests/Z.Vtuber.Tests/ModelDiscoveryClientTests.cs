using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>
/// 返工 #5：<see cref="ModelDiscoveryClient"/> 直测（假 HTTP 客户端）——OpenAI-like/Claude-like
/// 的 URL 与鉴权头、<c>data[].id</c> 解析去重排序、非 2xx / 坏 JSON / 超时 / 未知类型的异常映射。
/// </summary>
public class ModelDiscoveryClientTests
{
    private sealed class DiscoveryStubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class DiscoveryStubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static ModelDiscoveryClient CreateClient(DiscoveryStubHandler handler) =>
        new(new DiscoveryStubFactory(new HttpClient(handler)),
            Options.Create(new AiOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelDiscoveryClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TrainingProviderRecord Provider(
        string type, string endpoint = "https://api.example.com", string apiKey = "sk-test") =>
        new() { Name = "p", Type = type, Endpoint = endpoint, ApiKey = apiKey };

    [Fact]
    public async Task OpenAiLike_UsesModelsPathAndBearerAuth_ParsesDedupesSorts()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK,
                """{"data":[{"id":"gpt-4o"},{"id":"gpt-4o-audio"},{"id":"gpt-4o"}]}"""),
        };
        var client = CreateClient(handler);

        var models = await client.DiscoverAsync(
            Provider(TrainingProviderTypes.OpenAi, "https://api.openai.com/v1"));

        Assert.Equal("https://api.openai.com/v1/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("GET", handler.LastRequest.Method.Method);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal(new[] { "gpt-4o", "gpt-4o-audio" }, models.ToArray());
    }

    [Fact]
    public async Task ClaudeLike_UsesV1ModelsPathAndApiKeyHeaders()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"data":[{"id":"claude-3-5-sonnet"}]}"""),
        };
        var client = CreateClient(handler);

        var models = await client.DiscoverAsync(
            Provider(TrainingProviderTypes.Claude, "https://api.anthropic.com"));

        Assert.Equal("https://api.anthropic.com/v1/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("sk-test", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
        Assert.Equal(new[] { "claude-3-5-sonnet" }, models.ToArray());
    }

    [Fact]
    public async Task RootArray_IsParsed()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """[{"id":"b"},{"id":"a"}]"""),
        };

        var models = await CreateClient(handler).DiscoverAsync(Provider(TrainingProviderTypes.OpenAi));

        Assert.Equal(new[] { "a", "b" }, models.ToArray());
    }

    [Fact]
    public async Task MissingDataProperty_YieldsEmptyList()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"object":"list"}"""),
        };

        Assert.Empty(await CreateClient(handler).DiscoverAsync(Provider(TrainingProviderTypes.OpenAi)));
    }

    [Fact]
    public async Task NonSuccessStatus_ThrowsHttpRequestException()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => Json(HttpStatusCode.InternalServerError, "boom"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).DiscoverAsync(Provider(TrainingProviderTypes.OpenAi)));
    }

    [Fact]
    public async Task MalformedJson_ThrowsInvalidProviderResponse()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, "not-json{{"),
        };

        await Assert.ThrowsAsync<InvalidProviderResponseException>(
            () => CreateClient(handler).DiscoverAsync(Provider(TrainingProviderTypes.OpenAi)));
    }

    [Fact]
    public async Task Timeout_IsMappedToHttpRequestException()
    {
        var handler = new DiscoveryStubHandler
        {
            Handler = _ => throw new TaskCanceledException("timeout"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).DiscoverAsync(Provider(TrainingProviderTypes.OpenAi)));
    }

    [Fact]
    public async Task UnknownProviderType_ThrowsProviderTypeException()
    {
        var handler = new DiscoveryStubHandler { Handler = _ => Json(HttpStatusCode.OK, "{}") };

        await Assert.ThrowsAsync<ProviderTypeException>(
            () => CreateClient(handler).DiscoverAsync(Provider("gemini")));
    }
}
