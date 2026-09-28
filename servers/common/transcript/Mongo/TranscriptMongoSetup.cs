using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptMongoSetup(IMongoDatabase database, ILogger<TranscriptMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var tasks = Database.GetCollection<TranscriptTaskRecord>(TranscriptCollectionNames.TranscriptTasks);
        var taskUniqueKeys = Builders<TranscriptTaskRecord>.IndexKeys
            .Ascending(t => t.RecordingId)
            .Ascending(t => t.ModelId);
        await tasks.Indexes.CreateOneAsync(
            new CreateIndexModel<TranscriptTaskRecord>(
                taskUniqueKeys, new CreateIndexOptions { Unique = true, Name = "RecordingId_1_ModelId_1" }),
            cancellationToken: ct);

        var taskStatusKeys = Builders<TranscriptTaskRecord>.IndexKeys
            .Ascending(t => t.Status)
            .Ascending(t => t.Id);
        await tasks.Indexes.CreateOneAsync(
            new CreateIndexModel<TranscriptTaskRecord>(
                taskStatusKeys, new CreateIndexOptions { Name = "Status_1__id_1" }),
            cancellationToken: ct);

        var segments = Database.GetCollection<TranscriptSegmentRecord>(TranscriptCollectionNames.TranscriptSegments);
        var segmentKeys = Builders<TranscriptSegmentRecord>.IndexKeys
            .Ascending(s => s.TaskId)
            .Ascending(s => s.StartTime);
        await segments.Indexes.CreateOneAsync(
            new CreateIndexModel<TranscriptSegmentRecord>(
                segmentKeys, new CreateIndexOptions { Unique = true, Name = "TaskId_1_StartTime_1" }),
            cancellationToken: ct);

        var autoEnqueue = Database.GetCollection<TranscriptAutoEnqueueRecord>(TranscriptCollectionNames.TranscriptAutoEnqueue);
        var autoKeys = Builders<TranscriptAutoEnqueueRecord>.IndexKeys.Ascending(a => a.RoomId);
        await autoEnqueue.Indexes.CreateOneAsync(
            new CreateIndexModel<TranscriptAutoEnqueueRecord>(
                autoKeys, new CreateIndexOptions { Unique = true, Name = "RoomId_1" }),
            cancellationToken: ct);

        Logger.LogInformation("Ensured transcript collection indexes.");
    }

    public const string TranscriptModelIsDefaultCleanupMigrationKey = "transcript_model_isdefault_cleanup_v1";

    /// <summary>
    /// 清理 transcript_models 存量文档中已废弃的 IsDefault 元素：该字段已从实体移除，
    /// 残留元素会使 BsonClassMapSerializer 抛 FormatException（本次线上故障根因）。
    /// 用 BsonDocument 级 filter/update，因为强类型无法表达未映射字段。
    /// </summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var meta = MetaCollection;
        var markerExists = await meta.Find(r => r.Key == TranscriptModelIsDefaultCleanupMigrationKey)
            .AnyAsync(ct);
        if (markerExists)
        {
            Logger.LogInformation("Transcript model IsDefault cleanup already applied; skipping.");
            return;
        }

        var collection = Database.GetCollection<BsonDocument>(TranscriptCollectionNames.TranscriptModels);
        var filter = Builders<BsonDocument>.Filter.Exists("IsDefault", true);
        var update = Builders<BsonDocument>.Update.Unset("IsDefault");
        var result = await collection.UpdateManyAsync(filter, update, cancellationToken: ct);

        Logger.LogInformation(
            "Transcript model IsDefault cleanup matched {Matched} and modified {Modified} documents.",
            result.MatchedCount, result.ModifiedCount);

        await MarkMigrationAppliedAsync(TranscriptModelIsDefaultCleanupMigrationKey, ct);
        Logger.LogInformation("Transcript model IsDefault cleanup completed and marked as applied.");
    }

    /// <summary>
    /// 兼容旧调用名：与 <see cref="CleanupAsync"/> 等价。
    /// </summary>
    public Task CleanupTranscriptModelIsDefaultAsync(CancellationToken ct = default) => CleanupAsync(ct);
}
