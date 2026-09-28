using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class LiveStatusRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveStatusRecord> _collection =
        database.GetCollection<LiveStatusRecord>(LiveCollectionNames.LiveStatus);

    public Task InsertAsync(LiveStatusRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<LiveStatusRecord> GetLatestByRoomIdAsync(long roomId, CancellationToken ct = default) =>
        _collection.Find(l => l.RoomId == roomId)
            .SortByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<(long Total, IReadOnlyList<LiveStatusRecord> Items)> GetPageAsync(
        long roomId, int page, int pageSize, DateTime? startedAt = null, DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        var filter = Builders<LiveStatusRecord>.Filter.Eq(l => l.RoomId, roomId);
        if (startedAt.HasValue)
        {
            filter &= Builders<LiveStatusRecord>.Filter.Gte(l => l.CreatedAt, startedAt.Value);
        }
        if (endedAt.HasValue)
        {
            filter &= Builders<LiveStatusRecord>.Filter.Lte(l => l.CreatedAt, endedAt.Value);
        }
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _collection.Find(filter)
            .SortByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }
}
