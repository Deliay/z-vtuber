using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Weibo 域索引初始化。</summary>
public class WeiboMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EnsureIndexes_CreatesExpectedIndexes()
    {
        var setup = new WeiboMongoSetup(fixture.Database, NullLogger<WeiboMongoSetup>.Instance);
        await setup.EnsureIndexesAsync();

        var subscriptions = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<WeiboSubscriptionRecord>(WeiboCollectionNames.WeiboSubscriptions));
        Assert.Contains("Uid_1", subscriptions);

        var timeline = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<WeiboTimelineRecord>(WeiboCollectionNames.WeiboUserTimeline));
        Assert.Contains("MblogId_1", timeline);
        Assert.Contains("Uid_1_PubTs_-1", timeline);
        Assert.Contains("Uid_1_MblogId_-1", timeline);
    }
}
