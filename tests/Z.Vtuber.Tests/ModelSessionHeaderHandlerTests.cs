using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>
/// 模型服务固定请求头契约（<see cref="ModelSessionHeaderHandler"/>，挂在 named client <c>AiModel</c>）——
/// 与场景无关，所有模型服务调用（情绪识别、重新识别、模型发现）固定携带 <c>x-opencode-session</c>
/// 两个值：agent 标识 <c>z-vtuber-agent/1.0</c> + 每次请求新生成的随机 guid；
/// 同一请求的降级重试每次尝试都携带。
/// </summary>
public class ModelSessionHeaderHandlerTests
{
    private const string SessionHeader = "x-opencode-session";
    private const string AgentIdentifier = "z-vtuber-agent/1.0";
    private const string OpenAiBody = """{"choices":[{"message":{"content":"{}"}}]}""";
    private const string ClaudeBody = """{"content":[{"text":"{}"}]}""";
    private const string ModelListBody = """{"data":[{"id":"gpt-test"}]}""";

    /// <summary>最内层假服务：按请求序号（1 起）应答，同时快照请求头。</summary>
    private sealed class RecordingHandler(
        Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Dictionary<string, string[]>> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.ToDictionary(
                header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
            return Task.FromResult(responder(Requests.Count));
        }
    }

    private sealed class SessionHeaderStubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>与 Program.cs 同构：session handler 链在假服务之上。</summary>
    private static HttpClient ChainedClient(RecordingHandler inner) =>
        new(new ModelSessionHeaderHandler { InnerHandler = inner });

    private static TrainingProviderRecord Provider(string type) => new()
    {
        Name = "provider", Type = type, Endpoint = "http://llm.test", ApiKey = "secret",
    };

    private static IReadOnlyList<string> SessionValues(Dictionary<string, string[]> headers) =>
        headers.TryGetValue(SessionHeader, out var values) ? values : [];

    private static void AssertSessionHeader(Dictionary<string, string[]> headers)
    {
        var values = SessionValues(headers);
        Assert.Equal(2, values.Count);
        Assert.Equal(AgentIdentifier, values[0]);
        Assert.True(Guid.TryParse(values[1], out _), $"second value is not a guid: {values[1]}");
    }

    // ---- 与场景无关：三类模型服务调用都携带 ----

    [Fact]
    public async Task OpenAiLikeChatCall_CarriesSessionHeader()
    {
        var inner = new RecordingHandler(_ => Json(HttpStatusCode.OK, OpenAiBody));
        var client = new OpenAiLikeChatClient(
            ChainedClient(inner), new AiOptions(), NullLogger<OpenAiLikeChatClient>.Instance);

        await client.CompleteJsonAsync(Provider("openai"), new TrainingModelRecord { Model = "gpt-test" },
            new ChatPrompt("system", "user", null));

        AssertSessionHeader(Assert.Single(inner.Requests));
    }

    [Fact]
    public async Task ClaudeLikeChatCall_CarriesSessionHeader()
    {
        var inner = new RecordingHandler(_ => Json(HttpStatusCode.OK, ClaudeBody));
        var client = new ClaudeLikeChatClient(
            ChainedClient(inner), new AiOptions(), NullLogger<ClaudeLikeChatClient>.Instance);

        await client.CompleteJsonAsync(Provider("claude"), new TrainingModelRecord { Model = "gpt-test" },
            new ChatPrompt("system", "user", null));

        AssertSessionHeader(Assert.Single(inner.Requests));
    }

    [Fact]
    public async Task ModelDiscovery_CarriesSessionHeader()
    {
        var inner = new RecordingHandler(_ => Json(HttpStatusCode.OK, ModelListBody));
        var client = new ModelDiscoveryClient(
            new SessionHeaderStubFactory(ChainedClient(inner)),
            Options.Create(new AiOptions()),
            NullLogger<ModelDiscoveryClient>.Instance);

        var models = await client.DiscoverAsync(Provider("openai"));

        Assert.Equal(["gpt-test"], models);
        AssertSessionHeader(Assert.Single(inner.Requests));
    }

    [Fact]
    public async Task ResponseFormatRetry_CarriesSessionHeaderOnEveryAttempt()
    {
        var inner = new RecordingHandler(attempt => attempt == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":"response_format unsupported"}""")
            : Json(HttpStatusCode.OK, OpenAiBody));
        var client = new OpenAiLikeChatClient(
            ChainedClient(inner), new AiOptions(), NullLogger<OpenAiLikeChatClient>.Instance);

        await client.CompleteJsonAsync(Provider("openai"), new TrainingModelRecord { Model = "gpt-test" },
            new ChatPrompt("system", "user", null));

        Assert.Equal(2, inner.Requests.Count);
        foreach (var request in inner.Requests)
        {
            AssertSessionHeader(request);
        }
    }

    [Fact]
    public async Task Guid_IsRandomPerRequest()
    {
        var inner = new RecordingHandler(_ => Json(HttpStatusCode.OK, OpenAiBody));
        var client = new OpenAiLikeChatClient(
            ChainedClient(inner), new AiOptions(), NullLogger<OpenAiLikeChatClient>.Instance);
        var prompt = new ChatPrompt("system", "user", null);

        await client.CompleteJsonAsync(Provider("openai"), new TrainingModelRecord { Model = "gpt-test" }, prompt);
        await client.CompleteJsonAsync(Provider("openai"), new TrainingModelRecord { Model = "gpt-test" }, prompt);

        Assert.Equal(2, inner.Requests.Count);
        Assert.NotEqual(SessionValues(inner.Requests[0])[1], SessionValues(inner.Requests[1])[1]);
    }

    [Fact]
    public async Task SessionHeader_KeepsExistingHeadersAndAgentValueFirst()
    {
        var inner = new RecordingHandler(_ => Json(HttpStatusCode.OK, OpenAiBody));
        var client = new OpenAiLikeChatClient(
            ChainedClient(inner), new AiOptions(), NullLogger<OpenAiLikeChatClient>.Instance);

        await client.CompleteJsonAsync(Provider("openai"), new TrainingModelRecord { Model = "gpt-test" },
            new ChatPrompt("system", "user", null));

        var headers = Assert.Single(inner.Requests);
        // agent 标识在前、随机 guid 在后，且 Bearer 鉴权不受影响
        Assert.Equal(AgentIdentifier, SessionValues(headers)[0]);
        Assert.Contains("Authorization", headers.Keys);
    }
}
