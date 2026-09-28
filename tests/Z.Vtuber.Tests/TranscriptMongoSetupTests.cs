using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Transcript 域索引初始化与 IsDefault 清理迁移。</summary>
public class TranscriptMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptMongoSetup Setup() => new(fixture.Database, NullLogger<TranscriptMongoSetup>.Instance);

    [Fact]
    public async Task EnsureIndexes_CreatesExpectedIndexes_AndNoIsDefaultIndex()
    {
        await Setup().EnsureIndexesAsync();

        var tasks = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TranscriptTaskRecord>(TranscriptCollectionNames.TranscriptTasks));
        Assert.Contains("RecordingId_1_ModelId_1", tasks);
        Assert.Contains("Status_1__id_1", tasks);

        var segments = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TranscriptSegmentRecord>(TranscriptCollectionNames.TranscriptSegments));
        Assert.Contains("TaskId_1_StartTime_1", segments);

        var auto = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TranscriptAutoEnqueueRecord>(TranscriptCollectionNames.TranscriptAutoEnqueue));
        Assert.Contains("RoomId_1", auto);

        var models = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<TranscriptModelRecord>(TranscriptCollectionNames.TranscriptModels));
        Assert.DoesNotContain("IsDefault_1", models);
    }

    [Fact]
    public async Task CleanupAsync_UnsetsIsDefault_AndWritesMarkerOnce()
    {
        var collection = fixture.Database.GetCollection<BsonDocument>(TranscriptCollectionNames.TranscriptModels);
        var id = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new BsonDocument { { "_id", id }, { "Name", "model-a" }, { "IsDefault", true } });

        var setup = Setup();
        await setup.CleanupAsync();
        await setup.CleanupAsync();

        var record = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();
        Assert.False(record.Contains("IsDefault"));

        var meta = fixture.Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);
        Assert.Equal(1, await meta.CountDocumentsAsync(
            r => r.Key == TranscriptMongoSetup.TranscriptModelIsDefaultCleanupMigrationKey));
    }
}
