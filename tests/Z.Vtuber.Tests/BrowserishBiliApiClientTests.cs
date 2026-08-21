using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mikibot.Crawler.Http;
using Mikibot.Crawler.Http.Bilibili;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Configuration;
using Z.Vtuber.Crawlers;

namespace Z.Vtuber.Tests;

public sealed class BrowserishBiliApiClientTests
{
    private const string TestCookie = "test-cookie";
    private const long TestRoomId = 12345;
    private const long TestMid = 67890;

    private readonly BilibiliCrawlerOptions _options = new()
    {
        BrowserUserAgent = "TestChrome/1.0",
        BrowserMinDelaySeconds = 0,
        BrowserMaxDelaySeconds = 0,
        HttpTimeoutSeconds = 5
    };

    private IBilibiliAccountProvider CreateMockAccountProvider()
    {
        // 返回一个模拟的 IBilibiliAccountProvider，避免调用真实的 B 站 API
        return new MockBilibiliAccountProvider();
    }

    private sealed class MockBilibiliAccountProvider : IBilibiliAccountProvider
    {
        public async Task<IServiceProvider> GetProviderAsync(string cookie, CancellationToken ct = default)
        {
            var services = new ServiceCollection();
            
            // 创建一个模拟的 HttpClient 来返回有效的 nav 响应
            var mockHandler = new MockHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":0,"data":{"mid":12345,"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""")
                }));
            var httpClient = new HttpClient(mockHandler);
            
            var crawler = new Mikibot.Crawler.Http.Bilibili.BiliBasicInfoCrawler(httpClient, new CookieJar(cookie));
            var account = new BilibiliAccount(crawler);
            
            // 初始化 BilibiliAccount
            await account.InitializeAsync(ct);
            
            services.AddSingleton(account);
            return services.BuildServiceProvider();
        }
    }

    [Fact]
    public async Task GetLiveRoomInfoAsync_SendsCorrectRequest()
    {
        // Arrange
        var responseJson = """
        {
            "code": 0,
            "data": {
                "room_info": {
                    "room_id": 12345,
                    "title": "Test Stream",
                    "live_status": 1
                }
            }
        }
        """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.ToString().Contains("bilibili.com") == true &&
                !request.RequestUri.ToString().Contains("xlive"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Warmup</html>")
                };
            }

            Assert.Contains("xlive/web-room/v1/index/getInfoByRoom", request.RequestUri?.ToString());
            Assert.Contains($"room_id={TestRoomId}", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
        });

        var client = new BrowserishBiliApiClient(handler, _options, CreateMockAccountProvider());

        // Act
        var result = await client.GetLiveRoomInfoAsync(TestCookie, TestRoomId);

        // Assert
        Assert.Equal(TestRoomId, result.RoomId);
        Assert.Equal("Test Stream", result.Title);
        Assert.Equal(1, result.LiveStatus);
    }

    [Fact]
    public async Task GetNavInfoV2Async_SendsCorrectRequest()
    {
        // Arrange
        var responseJson = """
        {
            "code": 0,
            "data": {
                "mid": 12345,
                "uname": "TestUser"
            }
        }
        """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            // 预热请求
            if (request.RequestUri?.ToString().Contains("www.bilibili.com") == true &&
                !request.RequestUri.ToString().Contains("api."))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Warmup</html>")
                };
            }

            // Nav 请求
            Assert.Contains("x/web-interface/nav", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
        });

        var client = new BrowserishBiliApiClient(handler, _options, CreateMockAccountProvider());

        // Act
        var result = await client.GetNavInfoV2Async(TestCookie);

        // Assert
        Assert.Equal(12345, result.Mid);
    }

    [Fact]
    public async Task GetSpaceDynamicsAsync_SendsCorrectRequest()
    {
        // Arrange
        var responseJson = """
        {
            "code": 0,
            "data": {
                "has_more": true,
                "offset": "next-offset",
                "items": [
                    {
                        "id_str": "dynamic-1",
                        "type": "DYNAMIC_TYPE_AV"
                    },
                    {
                        "id_str": "dynamic-2",
                        "type": "DYNAMIC_TYPE_DRAW"
                    }
                ]
            }
        }
        """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.ToString().Contains("bilibili.com") == true &&
                !request.RequestUri.ToString().Contains("polymer"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Warmup</html>")
                };
            }

            Assert.Contains("x/polymer/web-dynamic/v1/feed/space", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
        });

        var client = new BrowserishBiliApiClient(handler, _options, CreateMockAccountProvider());

        // Act
        var result = await client.GetSpaceDynamicsAsync(TestCookie, TestMid, "test-offset");

        // Assert
        Assert.True(result.HasMore);
        Assert.Equal("next-offset", result.Offset);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("dynamic-1", result.Items[0].DynamicId);
        Assert.Equal("dynamic-2", result.Items[1].DynamicId);
    }

    [Fact]
    public async Task GetAccountInfoDataAsync_DelegatesDirectly()
    {
        // Arrange
        var expectedJson = """{"mid":67890,"name":"TestAccount"}""";
        var handler = new MockHttpMessageHandler(async request =>
        {
            Assert.Contains("ukamnads.icu", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(expectedJson)
            };
        });

        var client = new BrowserishBiliApiClient(handler, _options, CreateMockAccountProvider());

        // Act
        var result = await client.GetAccountInfoDataAsync(TestCookie, TestMid);

        // Assert
        Assert.Equal(67890, result.GetProperty("mid").GetInt64());
        Assert.Equal("TestAccount", result.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WarmupIsPerformedOnlyOnce()
    {
        // Arrange
        int warmupCount = 0;
        var handler = new MockHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.ToString().Contains("www.bilibili.com") == true &&
                !request.RequestUri.ToString().Contains("api."))
            {
                warmupCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Warmup</html>")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"room_info":{"room_id":1,"title":"t","live_status":0}}}""")
            };
        });

        var client = new BrowserishBiliApiClient(handler, _options, CreateMockAccountProvider());

        // Act
        await client.GetLiveRoomInfoAsync(TestCookie, 1);
        await client.GetLiveRoomInfoAsync(TestCookie, 2);

        // Assert
        Assert.Equal(1, warmupCount);
    }

    [Fact]
    public void Constructor_SetsDefaultHeaders()
    {
        // Arrange & Act
        var client = new BrowserishBiliApiClient(new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), _options, CreateMockAccountProvider());

        // Assert
        var defaultHeaders = client.HttpClient.DefaultRequestHeaders;
        Assert.Equal("TestChrome/1.0", defaultHeaders.UserAgent.ToString());
        Assert.Contains("application/json", defaultHeaders.Accept.ToString());
        Assert.Contains("zh-CN", defaultHeaders.AcceptLanguage.ToString());
        Assert.Contains("https://www.bilibili.com/", defaultHeaders.Referrer?.ToString());
    }

    [Fact]
    public async Task RequestsWithDelayRespectTiming()
    {
        // Arrange
        var options = new BilibiliCrawlerOptions
        {
            BrowserMinDelaySeconds = 1,
            BrowserMaxDelaySeconds = 1,
            HttpTimeoutSeconds = 5
        };

        var handler = new MockHttpMessageHandler(async request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"room_info":{"room_id":1,"title":"t","live_status":0}}}""")
            };
        });

        var client = new BrowserishBiliApiClient(handler, options, CreateMockAccountProvider());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        await client.GetLiveRoomInfoAsync(TestCookie, 1);
        await client.GetLiveRoomInfoAsync(TestCookie, 2);

        // Assert
        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds >= 900);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handlerFunc(request);
    }
}