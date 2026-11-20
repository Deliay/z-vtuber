using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Z.Vtuber.Auth.Health;

public sealed class RedisHealthCheck(
    RedisConnection connection,
    ILogger<RedisHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var redis = connection.Value;
        if (redis is null)
        {
            return HealthCheckResult.Unhealthy("Redis is not configured or unreachable.");
        }

        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis is reachable ({latency.TotalMilliseconds:F0} ms).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis connectivity check failed.");
            return HealthCheckResult.Unhealthy("Redis is unreachable.", ex);
        }
    }
}
