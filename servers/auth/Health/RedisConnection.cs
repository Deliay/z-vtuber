using StackExchange.Redis;

namespace Z.Vtuber.Auth.Health;

/// <summary>
/// 惰性 Redis 连接：连接失败不让进程启动失败；readiness 会报告不健康，
/// 限流 fail-open、选主按 <c>Auth:AllowDegradedRedis</c> 降级。
/// </summary>
public sealed class RedisConnection
{
    public RedisConnection(IConfiguration configuration, ILogger<RedisConnection> logger)
    {
        var connectionString = configuration.GetConnectionString("messaging");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("ConnectionStrings:messaging is missing; auth starts without Redis.");
            return;
        }

        try
        {
            Value = ConnectionMultiplexer.Connect(connectionString);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to connect to Redis; auth starts in degraded mode.");
        }
    }

    public IConnectionMultiplexer? Value { get; }
}
