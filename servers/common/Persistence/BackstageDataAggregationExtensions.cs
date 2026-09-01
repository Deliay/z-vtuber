using Microsoft.Extensions.DependencyInjection;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Common.Persistence;

public static class BackstageDataAggregationExtensions
{
    /// <summary>
    /// 注册管理后台数据聚合所需依赖：Cookie 帐号信息仓储（按接口注入，供探测落库与聚合接口消费）。
    /// 共享 nav 探测服务（CookieNavProbeService）仅抓取服务需要，由 crawler 工程自行注册。
    /// </summary>
    public static IServiceCollection AddBackstageDataAggregation(this IServiceCollection services)
    {
        services.AddSingleton<ICookieAccountInfoRepository, CookieAccountInfoRepository>();
        return services;
    }
}