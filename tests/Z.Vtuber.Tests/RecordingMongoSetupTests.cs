using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Recording 域索引初始化（recording_status + recordings，决策 8）。</summary>
public class RecordingMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EnsureIndexes_CreatesExpectedIndexes()
    {
        var setup = new RecordingMongoSetup(fixture.Database, NullLogger<RecordingMongoSetup>.Instance);
        await setup.EnsureIndexesAsync();

        var status = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<RecordingStatusRecord>(RecordingCollectionNames.RecordingStatus));
        Assert.Contains("RoomId_1", status);

        var recordings = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<RecordingRecord>(RecordingCollectionNames.Recordings));
        Assert.Contains("RoomId_1_StartedAt_1", recordings);
        Assert.Contains("RoomId_1_StartedAt_-1", recordings);
    }
}
