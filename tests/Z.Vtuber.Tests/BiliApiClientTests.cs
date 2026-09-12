using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Mikibot.Crawler;
using Mikibot.Crawler.Http;
using Z.Vtuber.Crawler.Crawlers;

namespace Z.Vtuber.Tests;

public sealed class BiliApiClientTests
{
    private sealed class StubAccountProvider(Func<IServiceProvider> build) : IBilibiliAccountProvider
    {
        public Task<IServiceProvider> GetProviderAsync(string cookieId, string cookie, CancellationToken ct = default) =>
            Task.FromResult(build());
    }

    private static IBilibiliAccountProvider ProviderWithGuardHandler(string json)
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        return new StubAccountProvider(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(new HttpClient(handler));
            services.AddSingleton(new CookieJar("cookie"));
            services.AddBilibiliCrawlers(addHttpClient: false);
            return services.BuildServiceProvider();
        });
    }

    [Fact]
    public async Task GetRoomGuardListAsync_ResolvesProviderAndParsesGuardInfo()
    {
        var json = """
        {"code":0,"data":{"info":{"num":50,"now":1,"page":1},"list":[
            {"uid":1,"ruid":2,"username":"u","rank":1,"face":"","is_alive":1,"guard_level":3,"medal_info":{"medal_name":"m","medal_level":2}}
        ],"top3":[]}}
        """;
        var client = new BiliApiClient(ProviderWithGuardHandler(json));

        var result = await client.GetRoomGuardListAsync("cookie", "cookie", 100, 200, 1);

        Assert.Equal(1, result.Info.PageCount);
        Assert.Single(result.List);
        Assert.Equal(1L, result.List[0].Uid);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handlerFunc(request);
    }
}