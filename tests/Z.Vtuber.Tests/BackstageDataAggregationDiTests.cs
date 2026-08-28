using Microsoft.Extensions.DependencyInjection;
using Z.Vtuber.Persistence;

namespace Z.Vtuber.Tests;

public sealed class BackstageDataAggregationDiTests
{
    [Fact]
    public void AddBackstageDataAggregation_RegistersInterfaceToConcreteSingleton()
    {
        var services = new ServiceCollection();
        services.AddBackstageDataAggregation();

        var descriptor = Assert.Single(
            services.Where(d => d.ServiceType == typeof(ICookieAccountInfoRepository)));
        Assert.Equal(typeof(CookieAccountInfoRepository), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}