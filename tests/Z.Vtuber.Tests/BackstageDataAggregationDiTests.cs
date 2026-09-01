using Microsoft.Extensions.DependencyInjection;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

public sealed class BackstageDataAggregationDiTests
{
    [Fact]
    public void AddBackstageDataAggregation_RegistersRepositoryInterfaceToConcreteSingleton()
    {
        var services = new ServiceCollection();
        services.AddBackstageDataAggregation();

        var repoDescriptor = Assert.Single(
            services.Where(d => d.ServiceType == typeof(ICookieAccountInfoRepository)));
        Assert.Equal(typeof(CookieAccountInfoRepository), repoDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, repoDescriptor.Lifetime);
    }
}