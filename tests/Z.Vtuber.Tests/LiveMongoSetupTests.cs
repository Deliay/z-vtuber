using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Live 域索引初始化（含 8 组集合）与方法顺序语义。</summary>
public class LiveMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private LiveMongoSetup Setup() => new(fixture.Database, NullLogger<LiveMongoSetup>.Instance);

    [Fact]
    public async Task EnsureIndexes_CreatesExpectedIndexes()
    {
        await Setup().EnsureIndexesAsync();

        var events = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<LiveEventRecord>(LiveCollectionNames.LiveEvent));
        Assert.Contains("RoomId_1_OccurredAt_-1_Type_1", events);

        var guards = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<LiveGuardRecord>(LiveCollectionNames.LiveGuards));
        Assert.Contains("Mid_1_Date_1", guards);
        Assert.Contains("RoomId_1_Date_1", guards);

        var textIndex = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex));
        Assert.Contains(LiveEventTextIndexRepository.SearchIndexName, textIndex);

        var subscriptions = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<SubscriptionRecord>(LiveCollectionNames.Subscriptions));
        Assert.Contains("Mid_1_Type_1", subscriptions);
    }

    [Fact]
    public async Task EnsureIndexes_IsIdempotent()
    {
        var setup = Setup();
        await setup.EnsureIndexesAsync();
        await setup.EnsureIndexesAsync();

        var guards = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<LiveGuardRecord>(LiveCollectionNames.LiveGuards));
        Assert.Single(guards, n => n == "Mid_1_Date_1");
    }

    [Fact]
    public async Task MigrateAsync_RunsSubscriptionSplit_AndCompositeIndex()
    {
        var setup = Setup();
        await setup.MigrateAsync();

        var subscriptions = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<SubscriptionRecord>(LiveCollectionNames.Subscriptions));
        Assert.Contains("Mid_1_Type_1", subscriptions);
    }
}
