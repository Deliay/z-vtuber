using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class LiveEventRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveEventRecord> _collection =
        database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);

    public Task InsertAsync(LiveEventRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public async Task<(long Total, IReadOnlyList<LiveEventRecord> Items)> GetPageAsync(
        long roomId, int page, int pageSize,
        IReadOnlyCollection<string>? types = null,
        DateTime? startedAt = null, DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        var filter = Builders<LiveEventRecord>.Filter.Eq(e => e.RoomId, roomId);
        if (types is { Count: > 0 })
        {
            filter &= Builders<LiveEventRecord>.Filter.In(e => e.Type, types);
        }
        if (startedAt.HasValue)
        {
            filter &= Builders<LiveEventRecord>.Filter.Gte(e => e.OccurredAt, startedAt.Value);
        }
        if (endedAt.HasValue)
        {
            filter &= Builders<LiveEventRecord>.Filter.Lte(e => e.OccurredAt, endedAt.Value);
        }

        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _collection.Find(filter)
            .SortBy(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }
}
