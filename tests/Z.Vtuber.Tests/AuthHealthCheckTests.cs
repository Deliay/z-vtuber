using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Auth.Health;

namespace Z.Vtuber.Tests;

/// <summary>Phase 8.2：Mongo / Redis 连通性检查（就绪矩阵的依赖项）。</summary>
public sealed class AuthHealthCheckTests : IClassFixture<MongoDbFixture>, IClassFixture<RedisFixture>
{
    private readonly MongoDbFixture _mongo;
    private readonly RedisFixture _redis;

    public AuthHealthCheckTests(MongoDbFixture mongo, RedisFixture redis)
    {
        _mongo = mongo;
        _redis = redis;
    }

    [Fact]
    public async Task MongoHealthCheck_ReportsHealthy()
    {
        var check = new MongoDbHealthCheck(_mongo.Database, NullLogger<MongoDbHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task RedisHealthCheck_ReportsHealthy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:messaging"] = _redis.ConnectionString
            })
            .Build();
        var connection = new RedisConnection(configuration, NullLogger<RedisConnection>.Instance);
        var check = new RedisHealthCheck(connection, NullLogger<RedisHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
