using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class LiveEventRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveEventRecord> _collection =
        database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);

    public Task InsertAsync(LiveEventRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<List<LiveEventRecord>> GetRangeAsync(
        long roomId, DateTime startedAt, DateTime endedAt, CancellationToken ct = default)
    {
        var filter = Builders<LiveEventRecord>.Filter.Eq(e => e.RoomId, roomId)
            & Builders<LiveEventRecord>.Filter.Gte(e => e.OccurredAt, startedAt)
            & Builders<LiveEventRecord>.Filter.Lte(e => e.OccurredAt, endedAt);
        return _collection.Find(filter)
            .SortBy(e => e.OccurredAt)
            .ToListAsync(ct);
    }
}
