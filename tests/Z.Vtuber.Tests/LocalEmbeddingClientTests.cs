using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Tests;

/// <summary>
/// 本地 embedding 客户端契约：Ollama <c>/api/embeddings</c>（body <c>model</c>+<c>prompt</c>，响应 <c>embedding</c>）、
/// 较新的 Ollama <c>/api/embed</c>（body <c>model</c>+<c>input</c>，响应 <c>embeddings:[[...]]</c>，取首个向量）与
/// OpenAI 兼容 <c>/v1/embeddings</c>（body <c>model</c>+<c>input</c>，响应 <c>data[0].embedding</c>）三种响应解析；
/// 非 2xx/超时/响应非法 → <see cref="EmbeddingUnavailableException"/>。
/// </summary>
public class LocalEmbeddingClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static LocalEmbeddingClient Create(StubHandler handler, Action<EmbeddingOptions>? configure = null)
    {
        var options = new EmbeddingOptions();
        configure?.Invoke(options);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://embed.test") };
        return new LocalEmbeddingClient(new StubHttpClientFactory(client), Options.Create(options), NullLogger<LocalEmbeddingClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task OllamaEndpoint_PostsModelAndPrompt_AndParsesEmbedding()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"embedding":[0.1,0.2,0.3]}"""),
        };
        var client = Create(handler);

        var vector = await client.GetEmbeddingAsync("你好");

        Assert.Equal([0.1f, 0.2f, 0.3f], vector);
        Assert.Equal("/api/embeddings", handler.Paths[0]);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("bge-m3", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("你好", body.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task OllamaEmbedEndpoint_PostsInputAndParsesNestedEmbeddings()
    {
        // 真实 Ollama /api/embed 返回：embeddings 为二维数组，附带 model/total_duration 等字段。
        const string responseBody =
            """{"model":"embeddinggemma","embeddings":[[-0.19399531,-0.012884921,0.031962056]],"total_duration":68737531,"load_duration":1772533,"prompt_eval_count":3}""";
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, responseBody) };
        var client = Create(handler, o => o.ApiPath = "/api/embed");

        var vector = await client.GetEmbeddingAsync("你好");

        Assert.Equal([-0.19399531f, -0.012884921f, 0.031962056f], vector);
        Assert.Equal("/api/embed", handler.Paths[0]);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("bge-m3", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("你好", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task OpenAiCompatibleEndpoint_UsesInputAndParsesData()
    {
        var handler = new StubHandler
        {
            Handler = _ => Json(HttpStatusCode.OK, """{"data":[{"embedding":[1,2]}]}"""),
        };
        var client = Create(handler, o => o.ApiPath = "/v1/embeddings");

        var vector = await client.GetEmbeddingAsync("hi");

        Assert.Equal([1f, 2f], vector);
        Assert.Equal("/v1/embeddings", handler.Paths[0]);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("hi", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task NonSuccess_ThrowsEmbeddingUnavailable()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.BadGateway, "{}") };
        var client = Create(handler);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => client.GetEmbeddingAsync("x"));
    }

    [Fact]
    public async Task InvalidResponse_ThrowsEmbeddingUnavailable()
    {
        var handler = new StubHandler { Handler = _ => Json(HttpStatusCode.OK, "not-json") };
        var client = Create(handler);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => client.GetEmbeddingAsync("x"));
    }

    [Fact]
    public async Task Timeout_ThrowsEmbeddingUnavailable()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var client = Create(handler);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => client.GetEmbeddingAsync("x"));
    }
}
