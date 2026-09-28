using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

public interface ITranscriptSegmentPersister
{
    /// <summary>
    /// 顺序写入 segment 后推进 task.Position（非事务，内部系统最终一致即可）；
    /// 重复 (TaskId, StartTime) 唯一索引冲突时幂等忽略并返回 false（不重复发布）。
    /// </summary>
    Task<bool> PersistAsync(ObjectId taskId, long start, long end, string text, CancellationToken ct = default);
}

public sealed class MongoTranscriptSegmentPersister : ITranscriptSegmentPersister
{
    private readonly IMongoCollection<TranscriptSegmentRecord> _segments;
    private readonly IMongoCollection<TranscriptTaskRecord> _tasks;

    public MongoTranscriptSegmentPersister(IMongoDatabase database)
    {
        _segments = database.GetCollection<TranscriptSegmentRecord>(TranscriptCollectionNames.TranscriptSegments);
        _tasks = database.GetCollection<TranscriptTaskRecord>(TranscriptCollectionNames.TranscriptTasks);
    }

    public async Task<bool> PersistAsync(
        ObjectId taskId, long start, long end, string text, CancellationToken ct = default)
    {
        try
        {
            await _segments.InsertOneAsync(
                new TranscriptSegmentRecord { TaskId = taskId, StartTime = start, EndTime = end, Text = text },
                cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // 撞 (TaskId, StartTime) 唯一索引：重跑/并发重复句，幂等忽略；position 仍尽力推进
            await AdvancePositionAsync(taskId, end, ct);
            return false;
        }

        await AdvancePositionAsync(taskId, end, ct);
        return true;
    }

    /// <summary>仅向前推进 position，避免重复句（更小的 end）回退断点；segment 与 position 非原子，靠唯一索引+幂等重跑兜底。</summary>
    private Task AdvancePositionAsync(ObjectId taskId, long end, CancellationToken ct)
    {
        var filter = Builders<TranscriptTaskRecord>.Filter.And(
            Builders<TranscriptTaskRecord>.Filter.Eq(t => t.Id, taskId),
            Builders<TranscriptTaskRecord>.Filter.Lt(t => t.Position, end));
        var update = Builders<TranscriptTaskRecord>.Update
            .Set(t => t.Position, end)
            .Set(t => t.UpdatedAt, DateTime.UtcNow);
        return _tasks.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
