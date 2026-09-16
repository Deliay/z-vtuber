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

    public async Task<RecordingRecord?> GetByIdAsync(ObjectId id, CancellationToken ct = default) =>
        await _collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct);

    public async Task<RecordingRecord?> GetByRoomIdStartedAtAsync(long roomId, DateTime startedAt, CancellationToken ct = default) =>
        await _collection.Find(r => r.RoomId == roomId && r.StartedAt == startedAt).FirstOrDefaultAsync(ct);

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
