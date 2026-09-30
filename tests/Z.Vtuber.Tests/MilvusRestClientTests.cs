using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Vectors;
using Xunit;

namespace Z.Vtuber.Tests;

/// <summary>
/// <see cref="MilvusRestClient"/> 契约（假 HTTP 服务）：<c>Authorization: Bearer {token}</c>、
/// endpoint 拼接、payload JSON 序列化、<c>code==0</c> 成功 / 非 0 业务错误（不抛）、
/// 非法 JSON / 超时 / 传输异常 → <see cref="VectorStoreUnavailableException"/>。
/// </summary>
public class MilvusRestClientTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return await responder(request, cancellationToken);
        }
    }

    private static MilvusRestClient Create(
        RecordingHandler handler, string token = "root:pw", int timeoutSeconds = 30,
        string endpoint = "http://milvus.test:19530") =>
        new(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new MilvusOptions
            {
                Endpoint = endpoint,
                Token = token,
                RequestTimeoutSeconds = timeoutSeconds,
            }));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SendsBearerToken_AndJoinsEndpoint_AndSerializesPayload()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json("""{"code":0,"message":""}""")));
        await Create(handler, token: "root:secret")
            .PostAsync("/v2/vectordb/collections/describe", new { collectionName = "item_text_embeddings" });

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "http://milvus.test:19530/v2/vectordb/collections/describe",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer root:secret", handler.LastRequest!.Headers.GetValues("Authorization").Single());
        Assert.Contains("item_text_embeddings", handler.LastBody);
    }

    [Fact]
    public async Task EmptyToken_OmitsAuthorizationHeader()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json("""{"code":0,"message":""}""")));
        await Create(handler, token: string.Empty).PostAsync("/v2/vectordb/entities/search", new { });

        Assert.False(handler.LastRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ParsesSuccessPayload()
    {
        var handler = new RecordingHandler(
            (_, _) => Task.FromResult(Json("""{"code":0,"message":"ok","data":{"x":1}}""")));

        var response = await Create(handler).PostAsync("/p", new { });

        Assert.True(response.IsSuccess);
        Assert.Equal(0, response.Code);
        Assert.Equal("ok", response.Message);
        Assert.Equal(1, response.Data!.Value.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task NonZeroCode_IsBusinessError_DoesNotThrow()
    {
        var handler = new RecordingHandler(
            (_, _) => Task.FromResult(Json("""{"code":1100,"message":"collection not found"}""")));

        var response = await Create(handler).PostAsync("/p", new { });

        Assert.False(response.IsSuccess);
        Assert.Equal(1100, response.Code);
        Assert.Equal("collection not found", response.Message);
    }

    [Fact]
    public async Task InvalidJson_ThrowsUnavailable()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json("this is not json")));

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => Create(handler).PostAsync("/p", new { }));
    }

    [Fact]
    public async Task TransportError_ThrowsUnavailable()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => Create(handler).PostAsync("/p", new { }));
    }

    [Fact]
    public async Task Timeout_ThrowsUnavailable()
    {
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            return Json("""{"code":0}""");
        });

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => Create(handler, timeoutSeconds: 1).PostAsync("/p", new { }));
    }
}
