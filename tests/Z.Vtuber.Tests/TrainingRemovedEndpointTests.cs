using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Z.Vtuber.Controllers;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// 端点移除回归：Milvus 彻底下线后 <c>vectors/rebuild</c> 双侧（training + api 代理）端点移除，
/// 路由不再匹配 → 404。用最小 <see cref="WebApplication"/>（仅挂载目标程序集控制器、不连接 Mongo/Redis）
/// 真实走一遍路由，锁定「端点已下线」。
/// </summary>
public class TrainingRemovedEndpointTests
{
    private static async Task AssertRouteReturnsNotFoundAsync(Assembly controllersAssembly, string url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(controllersAssembly);

        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var client = new HttpClient();
            using var response = await client.PostAsync(address + url, new StringContent("{}"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public Task TrainingVectorsRebuild_Removed_Returns404() =>
        AssertRouteReturnsNotFoundAsync(
            typeof(TrainingSynthesizeController).Assembly,
            "/api/training/datasets/ds1/vectors/rebuild");

    [Fact]
    public Task ApiProxyVectorsRebuild_Removed_Returns404() =>
        AssertRouteReturnsNotFoundAsync(
            typeof(TrainingProxyController).Assembly,
            "/api/training/datasets/ds1/vectors/rebuild");
}
