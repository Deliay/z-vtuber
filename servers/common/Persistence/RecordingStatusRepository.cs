using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class RecordingStatusRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<RecordingStatusRecord> _collection =
        database.GetCollection<RecordingStatusRecord>(MongoCollectionNames.RecordingStatus);

    public Task UpsertAsync(RecordingStatusRecord record, CancellationToken ct = default)
    {
        var filter = Builders<RecordingStatusRecord>.Filter.Eq(r => r.RoomId, record.RoomId);
        var update = Builders<RecordingStatusRecord>.Update
            .Set(r => r.RoomId, record.RoomId)
            .Set(r => r.State, record.State)
            .Set(r => r.HostName, record.HostName)
            .Set(r => r.SessionTitle, record.SessionTitle)
            .Set(r => r.Files, record.Files)
            .Set(r => r.Error, record.Error)
            .Set(r => r.UpdatedAt, record.UpdatedAt);
        return _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<RecordingStatusRecord?> GetByRoomIdAsync(long roomId, CancellationToken ct = default) =>
        await _collection.Find(r => r.RoomId == roomId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<RecordingStatusRecord>> GetAllAsync(CancellationToken ct = default) =>
        await _collection.Find(FilterDefinition<RecordingStatusRecord>.Empty).ToListAsync(ct);

    public async Task<IReadOnlyList<RecordingStatusRecord>> GetByRoomIdsAsync(
        IReadOnlyCollection<long> roomIds, CancellationToken ct = default)
    {
        if (roomIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<RecordingStatusRecord>.Filter.In(r => r.RoomId, roomIds);
        return await _collection.Find(filter).ToListAsync(ct);
    }

    /// <summary>
    /// 周期心跳刷新：仅更新 UpdatedAt，不改变最终状态。用于已结束的 not_live/error 状态的存活标记。
    /// </summary>
    public Task TouchAsync(long roomId, DateTime updatedAt, CancellationToken ct = default)
    {
        var filter = Builders<RecordingStatusRecord>.Filter.Eq(r => r.RoomId, roomId);
        var update = Builders<RecordingStatusRecord>.Update.Set(r => r.UpdatedAt, updatedAt);
        return _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
