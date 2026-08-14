using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class LiveStatusRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveStatusRecord> _collection =
        database.GetCollection<LiveStatusRecord>(MongoCollectionNames.LiveStatus);

    public Task InsertAsync(LiveStatusRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<LiveStatusRecord> GetLatestByRoomIdAsync(long roomId, CancellationToken ct = default) =>
        _collection.Find(l => l.RoomId == roomId)
            .SortByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<(long Total, IReadOnlyList<LiveStatusRecord> Items)> GetPageAsync(
        long roomId, int page, int pageSize, CancellationToken ct = default)
    {
        var filter = Builders<LiveStatusRecord>.Filter.Eq(l => l.RoomId, roomId);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _collection.Find(filter)
            .SortByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }
}
