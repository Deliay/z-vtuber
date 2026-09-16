using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Consumer.Tasks;

public interface ITranscriptSegmentPersister
{
    /// <summary>
    /// 在一个 Mongo 事务中 insert segment + 推进 task.Position；重复 (TaskId, StartTime) 唯一索引冲突时忽略并返回 false。
    /// </summary>
    Task<bool> PersistAsync(ObjectId taskId, long start, long end, string text, CancellationToken ct = default);
}

public sealed class MongoTranscriptSegmentPersister : ITranscriptSegmentPersister
{
    private readonly IMongoCollection<TranscriptSegmentRecord> _segments;
    private readonly IMongoCollection<TranscriptTaskRecord> _tasks;
    private readonly IMongoClient _client;

    public MongoTranscriptSegmentPersister(IMongoDatabase database)
    {
        _segments = database.GetCollection<TranscriptSegmentRecord>(MongoCollectionNames.TranscriptSegments);
        _tasks = database.GetCollection<TranscriptTaskRecord>(MongoCollectionNames.TranscriptTasks);
        _client = database.Client;
    }

    /// <summary>测试钩子：在 segment 写入后、position 更新前触发，用于验证事务回滚。</summary>
    internal Func<CancellationToken, Task>? AfterSegmentInsertHook { get; set; }

    public async Task<bool> PersistAsync(
        ObjectId taskId, long start, long end, string text, CancellationToken ct = default)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        try
        {
            await _segments.InsertOneAsync(
                session,
                new TranscriptSegmentRecord { TaskId = taskId, StartTime = start, EndTime = end, Text = text },
                cancellationToken: ct);

            if (AfterSegmentInsertHook is not null)
            {
                await AfterSegmentInsertHook(ct);
            }

            await _tasks.UpdateOneAsync(
                session,
                Builders<TranscriptTaskRecord>.Filter.Eq(t => t.Id, taskId),
                Builders<TranscriptTaskRecord>.Update
                    .Set(t => t.Position, end)
                    .Set(t => t.UpdatedAt, DateTime.UtcNow),
                cancellationToken: ct);

            await session.CommitTransactionAsync(ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            await session.AbortTransactionAsync(ct);
            return false;
        }
        catch
        {
            await session.AbortTransactionAsync(ct);
            throw;
        }
    }
}
