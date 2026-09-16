using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptAutoEnqueueRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TranscriptAutoEnqueueRecord> _collection =
        database.GetCollection<TranscriptAutoEnqueueRecord>(MongoCollectionNames.TranscriptAutoEnqueue);

    public Task UpsertAsync(long roomId, long mid, bool enabled, CancellationToken ct = default)
    {
        var filter = Builders<TranscriptAutoEnqueueRecord>.Filter.Eq(r => r.RoomId, roomId);
        var update = Builders<TranscriptAutoEnqueueRecord>.Update
            .Set(r => r.RoomId, roomId)
            .Set(r => r.Mid, mid)
            .Set(r => r.Enabled, enabled)
            .Set(r => r.UpdatedAt, DateTime.UtcNow);
        return _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<IReadOnlyList<TranscriptAutoEnqueueRecord>> GetAllAsync(CancellationToken ct = default) =>
        await _collection.Find(FilterDefinition<TranscriptAutoEnqueueRecord>.Empty).ToListAsync(ct);

    public async Task<TranscriptAutoEnqueueRecord?> GetByRoomIdAsync(long roomId, CancellationToken ct = default) =>
        await _collection.Find(r => r.RoomId == roomId).FirstOrDefaultAsync(ct);
}
