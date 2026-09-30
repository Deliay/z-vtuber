using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

/// <summary>
/// <see cref="MongoDbFixture"/> 的降本语义守护：
/// 索引只建一次、ResetAsync 清数据不清索引、清理面可裁剪、逐用例隔离零残留。
/// </summary>
public sealed class MongoDbFixtureTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ResetAsync_ClearsDataButKeepsIndexes()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveGuards);
        await collection.InsertOneAsync(new BsonDocument
        {
            { "Mid", 12345L },
            { "RoomId", 1L },
            { "Date", "2026-09-30" },
        });
        Assert.Equal(1, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

        await fixture.ResetAsync();

        Assert.Equal(0, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var indexes = await TestIndexHelpers.ListIndexNamesAsync(collection);
        Assert.Contains("Mid_1_Date_1", indexes);
    }

    [Fact]
    public async Task ResetAsync_IsolatesDataBetweenCases()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(TrainingCollectionNames.TrainingDatasets);
        await collection.InsertOneAsync(new BsonDocument { { "Name", "case-a" } });

        await fixture.ResetAsync();

        Assert.Equal(0, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task ResetAsync_WithExplicitCollections_TrimsCleanupSurface()
    {
        var guards = fixture.Database.GetCollection<BsonDocument>(LiveCollectionNames.LiveGuards);
        var recordings = fixture.Database.GetCollection<BsonDocument>(RecordingCollectionNames.Recordings);
        await guards.InsertOneAsync(new BsonDocument { { "Mid", 1L }, { "Date", "2026-09-30" } });
        await recordings.InsertOneAsync(new BsonDocument { { "RoomId", 1L } });

        await fixture.ResetAsync(LiveCollectionNames.LiveGuards);

        Assert.Equal(0, await guards.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(1, await recordings.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task ResetAsync_DoesNotReinitializeDroppedCollections()
    {
        const string name = TrainingCollectionNames.TrainingProviders;
        await fixture.Database.DropCollectionAsync(name);

        await fixture.ResetAsync();

        var names = await ListCollectionNamesAsync();
        Assert.DoesNotContain(name, names);

        // 恢复索引，避免影响同类中的其他用例。
        await fixture.EnsureIndexesAsync();
    }

    private async Task<List<string>> ListCollectionNamesAsync()
    {
        using var cursor = await fixture.Database.ListCollectionNamesAsync();
        return await cursor.ToListAsync();
    }
}
