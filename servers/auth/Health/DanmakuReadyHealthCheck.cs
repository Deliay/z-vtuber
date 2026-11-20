using Microsoft.Extensions.Diagnostics.HealthChecks;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Auth.Health;

/// <summary>至少一个配置房间事件源可用（leader 已连接或 follower 观察到 leader 租约）。</summary>
public sealed class DanmakuReadyHealthCheck(
    DanmakuHealthRegistry registry,
    IOptions<DanmakuOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ready = registry.IsReady(
            options.Value.RoomIds,
            TimeSpan.FromSeconds(options.Value.NotReadyGraceSeconds));
        return Task.FromResult(ready
            ? HealthCheckResult.Healthy("At least one danmaku room is available.")
            : HealthCheckResult.Unhealthy("No configured danmaku room is available."));
    }
}
