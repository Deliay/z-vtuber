using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptTaskRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TranscriptTaskRecord> _collection =
        database.GetCollection<TranscriptTaskRecord>(MongoCollectionNames.TranscriptTasks);

    public async Task<ObjectId> InsertAsync(TranscriptTaskRecord record, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record.Id;
    }

    public async Task<TranscriptTaskRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(t => t.Id == id).FirstOrDefaultAsync(ct);

    public async Task<TranscriptTaskRecord?> GetByRecordingAndModelAsync(
        ObjectId recordingId, ObjectId modelId, CancellationToken ct = default) =>
        await _collection.Find(t => t.RecordingId == recordingId && t.ModelId == modelId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<TranscriptTaskRecord>> ListAsync(
        ObjectId? recordingId, long? roomId, string? status, int limit, CancellationToken ct = default)
    {
        var filter = Builders<TranscriptTaskRecord>.Filter.Empty;
        if (recordingId.HasValue)
        {
            filter &= Builders<TranscriptTaskRecord>.Filter.Eq(t => t.RecordingId, recordingId.Value);
        }
        if (roomId.HasValue)
        {
            filter &= Builders<TranscriptTaskRecord>.Filter.Eq(t => t.RoomId, roomId.Value);
        }
        if (!string.IsNullOrEmpty(status))
        {
            filter &= Builders<TranscriptTaskRecord>.Filter.Eq(t => t.Status, status);
        }

        return await _collection.Find(filter)
            .SortByDescending(t => t.CreatedAt)
            .Limit(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 扫描查询：返回 PENDING 或 RUNNING 任务（RUNNING 是否可接管由调用方按锁存在性判定），按 _id 升序即入队顺序。
    /// </summary>
    public async Task<IReadOnlyList<TranscriptTaskRecord>> ListScannableAsync(int limit, CancellationToken ct = default)
    {
        var filter = Builders<TranscriptTaskRecord>.Filter.In(
            t => t.Status, new[] { TranscriptTaskStatus.Pending, TranscriptTaskStatus.Running });

        return await _collection.Find(filter)
            .SortBy(t => t.Id)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public Task SetStatusAsync(ObjectId id, string status, string? error = null, CancellationToken ct = default)
    {
        var update = Builders<TranscriptTaskRecord>.Update
            .Set(t => t.Status, status)
            .Set(t => t.Error, error)
            .Set(t => t.UpdatedAt, DateTime.UtcNow);
        return _collection.UpdateOneAsync(t => t.Id == id, update, cancellationToken: ct);
    }

    public Task UpdatePositionAsync(ObjectId id, long position, CancellationToken ct = default)
    {
        var update = Builders<TranscriptTaskRecord>.Update
            .Set(t => t.Position, position)
            .Set(t => t.UpdatedAt, DateTime.UtcNow);
        return _collection.UpdateOneAsync(t => t.Id == id, update, cancellationToken: ct);
    }

    public Task<long> CountByModelAndStatusAsync(ObjectId modelId, string status, CancellationToken ct = default) =>
        _collection.CountDocumentsAsync(
            t => t.ModelId == modelId && t.Status == status, cancellationToken: ct);
}
