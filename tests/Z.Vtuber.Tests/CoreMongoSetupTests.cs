using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

/// <summary>Core 基座：迁移 marker 读写与并发幂等。</summary>
public class CoreMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrationMarker_IsIdempotent_AndConcurrentSafe()
    {
        var a = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        var b = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);

        Assert.False(await a.IsMigrationAppliedAsync("core_test_marker"));

        await Task.WhenAll(
            a.MarkMigrationAppliedAsync("core_test_marker"),
            b.MarkMigrationAppliedAsync("core_test_marker"));

        Assert.True(await a.IsMigrationAppliedAsync("core_test_marker"));

        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);
        Assert.Equal(1, await meta.CountDocumentsAsync(r => r.Key == "core_test_marker"));
    }
}
