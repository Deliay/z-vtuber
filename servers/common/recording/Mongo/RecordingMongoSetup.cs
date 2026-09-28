using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class RecordingMongoSetup(IMongoDatabase database, ILogger<RecordingMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await EnsureRecordingStatusIndexesAsync(ct);
        await EnsureRecordingsIndexesAsync(ct);
        Logger.LogInformation("MongoDB recording indexes ensured for database {Database}", DatabaseName);
    }

    private Task EnsureRecordingStatusIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<RecordingStatusRecord>(RecordingCollectionNames.RecordingStatus);
        var keys = Builders<RecordingStatusRecord>.IndexKeys.Ascending(r => r.RoomId);
        var options = new CreateIndexOptions { Unique = true, Name = "RoomId_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<RecordingStatusRecord>(keys, options), cancellationToken: ct);
    }

    private async Task EnsureRecordingsIndexesAsync(CancellationToken ct)
    {
        var recordings = Database.GetCollection<RecordingRecord>(RecordingCollectionNames.Recordings);
        // 幂等键 RoomId+StartedAt 唯一索引：存量清洗/滚动更新双实例并发插入时兜底，DuplicateKey 才算已存在
        await DropIndexIfExistsAsync(recordings, "RoomId_1_StartedAt_-1", ct);
        var recordingIdempotencyKeys = Builders<RecordingRecord>.IndexKeys
            .Ascending(r => r.RoomId)
            .Ascending(r => r.StartedAt);
        await recordings.Indexes.CreateOneAsync(
            new CreateIndexModel<RecordingRecord>(
                recordingIdempotencyKeys, new CreateIndexOptions { Unique = true, Name = "RoomId_1_StartedAt_1" }),
            cancellationToken: ct);

        var recordingListKeys = Builders<RecordingRecord>.IndexKeys
            .Ascending(r => r.RoomId)
            .Descending(r => r.StartedAt);
        await recordings.Indexes.CreateOneAsync(
            new CreateIndexModel<RecordingRecord>(
                recordingListKeys, new CreateIndexOptions { Name = "RoomId_1_StartedAt_-1" }),
            cancellationToken: ct);
    }
}
