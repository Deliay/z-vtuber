using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class RecordingRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<RecordingRecord> _collection =
        database.GetCollection<RecordingRecord>(MongoCollectionNames.Recordings);

    public async Task<ObjectId> InsertAsync(RecordingRecord record, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(record, cancellationToken: ct);
        return record.Id;
    }

    public Task SetEndedAtAsync(ObjectId id, DateTime endedAt, CancellationToken ct = default)
    {
        var update = Builders<RecordingRecord>.Update.Set(r => r.EndedAt, endedAt);
        return _collection.UpdateOneAsync(r => r.Id == id, update, cancellationToken: ct);
    }

    /// <summary>
    /// 条件回写 EndedAt（仅当当前仍为 null）：返回是否由本次调用完成收尾。
    /// 用于孤儿场次幂等收尾，保证多实例/重复调用下只发布一次 finished。
    /// </summary>
    public async Task<bool> TrySetEndedAtAsync(ObjectId id, DateTime endedAt, CancellationToken ct = default)
    {
        var filter = Builders<RecordingRecord>.Filter.And(
            Builders<RecordingRecord>.Filter.Eq(r => r.Id, id),
            Builders<RecordingRecord>.Filter.Eq(r => r.EndedAt, null));
        var update = Builders<RecordingRecord>.Update.Set(r => r.EndedAt, endedAt);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <summary>查询未收尾（EndedAt 为 null）的场次，可按 RoomId 过滤。</summary>
    public async Task<IReadOnlyList<RecordingRecord>> ListOpenAsync(long? roomId = null, CancellationToken ct = default)
    {
        var filter = Builders<RecordingRecord>.Filter.Eq(r => r.EndedAt, null);
        if (roomId.HasValue)
        {
            filter &= Builders<RecordingRecord>.Filter.Eq(r => r.RoomId, roomId.Value);
        }

        return await _collection.Find(filter)
            .SortBy(r => r.StartedAt)
            .ToListAsync(ct);
    }

    public async Task<RecordingRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct);

    public async Task<RecordingRecord?> GetByRoomIdStartedAtAsync(long roomId, DateTime startedAt, CancellationToken ct = default) =>
        await _collection.Find(r => r.RoomId == roomId && r.StartedAt == startedAt).FirstOrDefaultAsync(ct);

    /// <summary>查询指定房间全部场次，按 StartedAt 倒序（供录播文件列表聚合）。</summary>
    public async Task<IReadOnlyList<RecordingRecord>> ListByRoomAsync(long roomId, CancellationToken ct = default) =>
        await _collection.Find(r => r.RoomId == roomId)
            .SortByDescending(r => r.StartedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RecordingRecord>> ListAsync(
        long? roomId, int limit, DateTime? before, CancellationToken ct = default)
    {
        var filter = Builders<RecordingRecord>.Filter.Empty;
        if (roomId.HasValue)
        {
            filter &= Builders<RecordingRecord>.Filter.Eq(r => r.RoomId, roomId.Value);
        }
        if (before.HasValue)
        {
            filter &= Builders<RecordingRecord>.Filter.Lt(r => r.StartedAt, before.Value);
        }

        return await _collection.Find(filter)
            .SortByDescending(r => r.StartedAt)
            .Limit(limit)
            .ToListAsync(ct);
    }
}
