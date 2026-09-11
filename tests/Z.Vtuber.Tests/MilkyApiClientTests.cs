using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Notifier.Milky;

namespace Z.Vtuber.Tests;

public class MilkyApiClientTests
{
    private sealed class RequestCapturingHandler : DelegatingHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HandlerFunc { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return HandlerFunc?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (MilkyApiClient Client, RequestCapturingHandler Handler) CreateClient(MilkyOptions? options = null)
    {
        var handler = new RequestCapturingHandler();
        var httpClient = new MilkyHttpClient(handler, options ?? new MilkyOptions
        {
            Endpoint = "http://localhost:8080",
            AccessToken = "test-token",
        });
        return (new MilkyApiClient(httpClient, NullLogger<MilkyApiClient>.Instance), handler);
    }

    private static HttpResponseMessage CreateMilkyResponse(object data)
    {
        var response = new
        {
            status = "ok",
            retcode = 0,
            data = data,
            message = (string?)null,
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage CreateMilkyErrorResponse(string message, int retcode = 1)
    {
        var response = new
        {
            status = "failed",
            retcode = retcode,
            data = (object?)null,
            message = message,
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task GetLoginInfoAsync_Success_ReturnsLoginInfo()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/api/get_login_info"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Assert.Null(request.Content.Headers.ContentType?.CharSet);

            return Task.FromResult(CreateMilkyResponse(new { uin = 12345, nickname = "test-bot" }));
        };

        var result = await client.GetLoginInfoAsync();

        Assert.Equal(12345, result.UserId);
        Assert.Equal("test-bot", result.Nickname);
    }

    [Fact]
    public async Task GetLoginInfoAsync_NoToken_OmitsAuthorizationHeader()
    {
        var (client, handler) = CreateClient(new MilkyOptions
        {
            Endpoint = "http://localhost:8080",
            AccessToken = "",
        });
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);

            return Task.FromResult(CreateMilkyResponse(new { uin = 12345, nickname = "test-bot" }));
        };

        var result = await client.GetLoginInfoAsync();

        Assert.Equal(12345, result.UserId);
    }

    [Fact]
    public async Task GetLoginInfoAsync_HttpError_Throws()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLoginInfoAsync());
    }

    [Fact]
    public async Task GetLoginInfoAsync_MilkyError_Throws()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(CreateMilkyErrorResponse("Login failed"));
        };

        await Assert.ThrowsAsync<Milky.Net.Model.MilkyException>(() => client.GetLoginInfoAsync());
    }

    [Fact]
    public async Task GetGroupListAsync_ReturnsGroups()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/api/get_group_list"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);

            var groups = new object[]
            {
                new { group_id = 123456, group_name = "运维群" },
                new { group_id = 789012, group_name = "测试群" },
            };
            return Task.FromResult(CreateMilkyResponse(new { groups }));
        };

        var result = await client.GetGroupListAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(123456, result[0].GroupId);
        Assert.Equal("运维群", result[0].GroupName);
        Assert.Equal(789012, result[1].GroupId);
        Assert.Equal("测试群", result[1].GroupName);
    }

    [Fact]
    public async Task GetGroupListAsync_EmptyList_ReturnsEmpty()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(CreateMilkyResponse(new { groups = Array.Empty<object>() }));
        };

        var result = await client.GetGroupListAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task SendGroupMessageAsync_SendsCorrectRequest()
    {
        long? capturedGroupId = null;
        string? capturedText = null;

        var (client, handler) = CreateClient();
        handler.HandlerFunc = async (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/api/send_group_message"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);

            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            capturedGroupId = body.GetProperty("group_id").GetInt64();
            var messages = body.GetProperty("message");
            Assert.Equal(1, messages.GetArrayLength());
            var segment = messages[0];
            Assert.Equal("text", segment.GetProperty("type").GetString());
            capturedText = segment.GetProperty("data").GetProperty("text").GetString();
            return CreateMilkyResponse(new { message_id = 123 });
        };

        await client.SendGroupMessageAsync(123456, "Hello World");

        Assert.Equal(123456, capturedGroupId);
        Assert.Equal("Hello World", capturedText);
    }

    [Fact]
    public async Task SendGroupMessageAsync_NoXmlEscape()
    {
        string? capturedText = null;

        var (client, handler) = CreateClient();
        handler.HandlerFunc = async (request, ct) =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            var messages = body.GetProperty("message");
            var segment = messages[0];
            capturedText = segment.GetProperty("data").GetProperty("text").GetString();
            return CreateMilkyResponse(new { message_id = 123 });
        };

        await client.SendGroupMessageAsync(123456, "Hello <world> & \"test\"");

        Assert.Equal("Hello <world> & \"test\"", capturedText);
    }

    [Fact]
    public async Task SendGroupMessageAsync_HttpError_Throws()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendGroupMessageAsync(123456, "test"));
    }

    [Fact]
    public async Task SendGroupMessageAsync_MilkyError_Throws()
    {
        var (client, handler) = CreateClient();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(CreateMilkyErrorResponse("Send failed"));
        };

        await Assert.ThrowsAsync<Milky.Net.Model.MilkyException>(() => client.SendGroupMessageAsync(123456, "test"));
    }

    [Fact]
    public async Task AllRequests_HaveCorrectContentType_NoCharset()
    {
        string? capturedContentType = null;

        var (client, handler) = CreateClient();
        handler.HandlerFunc = async (request, ct) =>
        {
            capturedContentType = request.Content?.Headers.ContentType?.ToString();
            return CreateMilkyResponse(new { uin = 1, nickname = "test" });
        };

        await client.GetLoginInfoAsync();

        Assert.Equal("application/json", capturedContentType);
    }
}
