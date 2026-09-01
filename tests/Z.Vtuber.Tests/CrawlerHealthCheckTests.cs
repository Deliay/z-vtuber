using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Crawler;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class CrawlerHealthCheckTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task CheckHealth_MongoReachable_ReturnsHealthy()
    {
        var check = new MongoDbHealthCheck(fixture.Database, NullLogger<MongoDbHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealth_MongoUnreachable_ReturnsUnhealthy()
    {
        var client = new MongoClient(new MongoUrl("mongodb://localhost:1/?serverSelectionTimeoutMS=1000"));
        var database = client.GetDatabase("unreachable");
        var check = new MongoDbHealthCheck(database, NullLogger<MongoDbHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}