using MongoDB.Bson;
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
        long? minPriceGoldSeeds = null,
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
        if (minPriceGoldSeeds.HasValue)
        {
            var notGiftFilter = Builders<LiveEventRecord>.Filter.Ne(e => e.Type, "gift");
            var priceFilter = Builders<LiveEventRecord>.Filter.Gte(e => e.Price, minPriceGoldSeeds.Value);
            filter &= notGiftFilter | priceFilter;
        }

        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);

        var sort = Builders<LiveEventRecord>.Sort.Descending(e => e.OccurredAt);
        var items = await _collection.Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }

    public async Task<IReadOnlyList<LiveEventRecord>> GetByIdsAsync(
        IReadOnlyCollection<ObjectId> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<LiveEventRecord>();
        }

        var filter = Builders<LiveEventRecord>.Filter.In(e => e.Id, ids);
        return await _collection.Find(filter).ToListAsync(ct);
    }
}
