using Microsoft.Extensions.DependencyInjection;
using Z.Vtuber.Services;

namespace Z.Vtuber.Persistence;

public static class BackstageDataAggregationExtensions
{
    /// <summary>
    /// 注册管理后台数据聚合所需依赖：
    /// Cookie 帐号信息仓储（按接口注入，供探测落库与聚合接口消费）与共享 nav 探测服务（供 CookieProbeJob 与 Cookie 首次检出路径消费）。
    /// CookiePoolService 经 IServiceProvider 惰性解析 <see cref="ICookieNavProbeService"/>，避免 CookiePool → IBiliApiClient → CookiePool 构造循环依赖。
    /// </summary>
    public static IServiceCollection AddBackstageDataAggregation(this IServiceCollection services)
    {
        services.AddSingleton<ICookieAccountInfoRepository, CookieAccountInfoRepository>();
        services.AddSingleton<ICookieNavProbeService, CookieNavProbeService>();
        return services;
    }
}