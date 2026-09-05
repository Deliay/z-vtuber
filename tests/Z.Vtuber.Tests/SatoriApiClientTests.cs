using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Notifier.Satori;

namespace Z.Vtuber.Tests;

public class SatoriApiClientTests
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

    private static SatoriApiClient CreateClient(MockHttpMessageHandler handler, SatoriOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var opts = Options.Create(options ?? new SatoriOptions
        {
            Endpoint = "http://localhost:8080",
            Token = "test-token",
        });
        return new SatoriApiClient(httpClient, opts, NullLogger<SatoriApiClient>.Instance);
    }

    [Fact]
    public async Task LoginGetAsync_Success_ReturnsLoginInfo()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            Assert.Equal(new Uri("http://localhost:8080/v1/login.get"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());

            // 不应携带 Satori-Platform/Satori-User-ID 头
            Assert.False(request.Headers.Contains("Satori-Platform"));
            Assert.False(request.Headers.Contains("Satori-User-ID"));

            var loginInfo = new LoginInfo
            {
                Platform = "onebot",
                User = new UserInfo { Id = "12345" },
                Status = 1, // ONLINE
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(loginInfo), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler);

        var result = await client.LoginGetAsync();

        Assert.Equal("onebot", result.Platform);
        Assert.Equal("12345", result.User.Id);
        Assert.Equal(1, result.Status);
    }

    [Fact]
    public async Task LoginGetAsync_CachesResult()
    {
        var callCount = 0;
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            callCount++;
            var loginInfo = new LoginInfo
            {
                Platform = "onebot",
                User = new UserInfo { Id = "12345" },
                Status = 1,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(loginInfo), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler);

        await client.LoginGetAsync();
        await client.LoginGetAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task LoginGetAsync_NonOnlineStatus_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            var loginInfo = new LoginInfo
            {
                Platform = "onebot",
                User = new UserInfo { Id = "12345" },
                Status = 0, // OFFLINE
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(loginInfo), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginGetAsync());
        Assert.Contains("status 0", ex.Message);
    }

    [Fact]
    public async Task LoginGetAsync_HttpError_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        };

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.LoginGetAsync());
    }

    [Fact]
    public async Task GetGuildListAsync_PaginatesCorrectly()
    {
        var callCount = 0;
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = async (request, ct) =>
        {
            callCount++;

            // 跳过 login.get
            if (request.RequestUri?.PathAndQuery == "/v1/login.get")
            {
                var loginResult = new { platform = "onebot", user = new { id = "123" }, status = 1 };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(loginResult), System.Text.Encoding.UTF8, "application/json"),
                };
            }

            if (callCount == 2)
            {
                var result = new { data = new[] { new { id = "guild1", name = "Guild 1" } }, next = "page2" };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(result), System.Text.Encoding.UTF8, "application/json"),
                };
            }
            else
            {
                var result = new { data = new[] { new { id = "guild2", name = "Guild 2" } }, next = (string?)null };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(result), System.Text.Encoding.UTF8, "application/json"),
                };
            }
        };

        var client = CreateClient(handler);
        // 先登录以获取 platform/selfId
        await client.LoginGetAsync();

        var guilds = await client.GetGuildListAsync();

        Assert.Equal(2, guilds.Count);
        Assert.Equal("guild1", guilds[0].Id);
        Assert.Equal("guild2", guilds[1].Id);
    }

    [Fact]
    public async Task GetChannelListAsync_PaginatesCorrectly()
    {
        var callCount = 0;
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            callCount++;

            // 跳过 login.get
            if (request.RequestUri?.PathAndQuery == "/v1/login.get")
            {
                var loginResult = new { platform = "onebot", user = new { id = "123" }, status = 1 };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(loginResult), System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (callCount == 2)
            {
                var result = new { data = new[] { new { id = "channel1", name = "Channel 1" } }, next = "page2" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(result), System.Text.Encoding.UTF8, "application/json"),
                });
            }
            else
            {
                var result = new { data = new[] { new { id = "channel2", name = "Channel 2" } }, next = (string?)null };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(result), System.Text.Encoding.UTF8, "application/json"),
                });
            }
        };

        var client = CreateClient(handler);
        await client.LoginGetAsync();

        var channels = await client.GetChannelListAsync("guild1");

        Assert.Equal(2, channels.Count);
        Assert.Equal("channel1", channels[0].Id);
        Assert.Equal("channel2", channels[1].Id);
    }

    [Fact]
    public async Task SendMessageAsync_SendsCorrectRequest()
    {
        string? capturedChannelId = null;
        string? capturedContent = null;

        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = async (request, ct) =>
        {
            if (request.RequestUri?.PathAndQuery == "/v1/login.get")
            {
                var loginResult = new { platform = "onebot", user = new { id = "123" }, status = 1 };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(loginResult), System.Text.Encoding.UTF8, "application/json"),
                };
            }
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            capturedChannelId = body.GetProperty("channel_id").GetString();
            capturedContent = body.GetProperty("content").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var client = CreateClient(handler);
        await client.LoginGetAsync();

        await client.SendMessageAsync("channel123", "Hello <world>");

        Assert.Equal("channel123", capturedChannelId);
        Assert.Equal("Hello &lt;world&gt;", capturedContent);
    }

    [Fact]
    public async Task EnvOverride_SkipsLoginDiscovery()
    {
        var callCount = 0;
        var handler = new MockHttpMessageHandler();
        handler.HandlerFunc = (request, ct) =>
        {
            callCount++;
            // 第一次调用应该是 guild.list，不是 login.get
            if (request.RequestUri?.PathAndQuery == "/v1/login.get")
            {
                throw new InvalidOperationException("Should not call login.get");
            }
            // 返回空的 guild 列表
            var result = new { data = Array.Empty<object>(), next = (string?)null };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result), System.Text.Encoding.UTF8, "application/json"),
            });
        };

        var client = CreateClient(handler, new SatoriOptions
        {
            Endpoint = "http://localhost:8080",
            Token = "test-token",
            Platform = "env-platform",
            SelfId = "env-self-id",
        });

        var guilds = await client.GetGuildListAsync();

        Assert.Empty(guilds);
        // 确保没有调用 login.get
        Assert.DoesNotContain("login.get", handler.HandlerFunc.Method.Name);
    }
}
