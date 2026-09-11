using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Notifier.Milky;

namespace Z.Vtuber.Tests;

public class MilkyApiClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HandlerFunc { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return HandlerFunc?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static MilkyApiClient CreateClient(MockHttpMessageHandler handler, MilkyOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var opts = Options.Create(options ?? new MilkyOptions
        {
            Endpoint = "http://localhost:8080",
            AccessToken = "test-token",
        });
        return new MilkyApiClient(httpClient, opts, NullLogger<MilkyApiClient>.Instance);
    }

    [Fact]
    public async Task GetLoginInfoAsync_Success_ReturnsLoginInfo()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/api/get_login_info"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());

            var loginInfo = new { user_id = 12345, nickname = "test-bot" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(loginInfo), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler);

        var result = await client.GetLoginInfoAsync();

        Assert.Equal(12345, result.UserId);
        Assert.Equal("test-bot", result.Nickname);
    }

    [Fact]
    public async Task GetLoginInfoAsync_NoToken_OmitsAuthorizationHeader()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.False(request.Headers.Contains("Authorization"));

            var loginInfo = new { user_id = 12345, nickname = "test-bot" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(loginInfo), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler, new MilkyOptions
        {
            Endpoint = "http://localhost:8080",
            AccessToken = "",
        });

        var result = await client.GetLoginInfoAsync();

        Assert.Equal(12345, result.UserId);
    }

    [Fact]
    public async Task GetLoginInfoAsync_HttpError_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        };

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLoginInfoAsync());
    }

    [Fact]
    public async Task GetGroupListAsync_ReturnsGroups()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/api/get_group_list"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());

            var groups = new
            {
                data = new[]
                {
                    new { group_id = 123456, group_name = "运维群" },
                    new { group_id = 789012, group_name = "测试群" },
                }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(groups), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler);

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
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            var groups = new { data = Array.Empty<object>() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(groups), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler);

        var result = await client.GetGroupListAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task SendGroupMessageAsync_SendsCorrectRequest()
    {
        long? capturedGroupId = null;
        string? capturedMessage = null;

        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = async (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/api/send_group_message"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());

            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            capturedGroupId = body.GetProperty("group_id").GetInt64();
            var messageArray = body.GetProperty("message");
            Assert.Equal(1, messageArray.GetArrayLength());
            var messageSegment = messageArray[0];
            Assert.Equal("text", messageSegment.GetProperty("type").GetString());
            capturedMessage = messageSegment.GetProperty("data").GetProperty("text").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var client = CreateClient(handler);

        await client.SendGroupMessageAsync(123456, "Hello World");

        Assert.Equal(123456, capturedGroupId);
        Assert.Equal("Hello World", capturedMessage);
    }

    [Fact]
    public async Task SendGroupMessageAsync_NoXmlEscape()
    {
        string? capturedMessage = null;

        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = async (request, ct) =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            var messageArray = body.GetProperty("message");
            var messageSegment = messageArray[0];
            capturedMessage = messageSegment.GetProperty("data").GetProperty("text").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var client = CreateClient(handler);

        await client.SendGroupMessageAsync(123456, "Hello <world> & \"test\"");

        Assert.Equal("Hello <world> & \"test\"", capturedMessage);
    }

    [Fact]
    public async Task SendGroupMessageAsync_HttpError_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        };

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendGroupMessageAsync(123456, "test"));
    }
}