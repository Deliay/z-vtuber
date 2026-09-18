using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptAutoEnqueueRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TranscriptAutoEnqueueRecord> _collection =
        database.GetCollection<TranscriptAutoEnqueueRecord>(MongoCollectionNames.TranscriptAutoEnqueue);

    public Task UpsertAsync(long roomId, long mid, bool enabled, ObjectId? modelId = null, CancellationToken ct = default)
    {
        var filter = Builders<TranscriptAutoEnqueueRecord>.Filter.Eq(r => r.RoomId, roomId);
        var update = Builders<TranscriptAutoEnqueueRecord>.Update
            .Set(r => r.RoomId, roomId)
            .Set(r => r.Mid, mid)
            .Set(r => r.Enabled, enabled)
            .Set(r => r.UpdatedAt, DateTime.UtcNow);

        // 仅在显式传入时更新绑定（enable=false 未传 modelId 时保留原绑定，避免误清）
        if (modelId.HasValue)
        {
            update = update.Set(r => r.ModelId, modelId.Value);
        }

        return _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<IReadOnlyList<TranscriptAutoEnqueueRecord>> GetAllAsync(CancellationToken ct = default) =>
        await _collection.Find(FilterDefinition<TranscriptAutoEnqueueRecord>.Empty).ToListAsync(ct);

    public async Task<TranscriptAutoEnqueueRecord?> GetByRoomIdAsync(long roomId, CancellationToken ct = default) =>
        await _collection.Find(r => r.RoomId == roomId).FirstOrDefaultAsync(ct);

    /// <summary>物理删除指定房间的自动识别设置；返回是否删到记录。</summary>
    public async Task<bool> DeleteByRoomIdAsync(long roomId, CancellationToken ct = default)
    {
        var result = await _collection.DeleteOneAsync(r => r.RoomId == roomId, ct);
        return result.DeletedCount == 1;
    }
}
