using Microsoft.Extensions.DependencyInjection;

namespace Z.Vtuber.Persistence;

public static class BackstageDataAggregationExtensions
{
    /// <summary>
    /// 注册管理后台数据聚合所需依赖：Cookie 帐号信息仓储（供探测落库与聚合接口消费）。
    /// 仓储统一以接口注入（<see cref="ICookieAccountInfoRepository"/>），避免「具体类注册→接口解析」失败。
    /// </summary>
    public static IServiceCollection AddBackstageDataAggregation(this IServiceCollection services)
    {
        services.AddSingleton<ICookieAccountInfoRepository, CookieAccountInfoRepository>();
        return services;
    }
}