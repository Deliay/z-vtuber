using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class SpaceTimelineRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<SpaceTimelineRecord> _collection =
        database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);

    public async Task<HashSet<string>> GetExistingDynamicIdsAsync(
        IReadOnlyCollection<string> dynamicIds, CancellationToken ct = default)
    {
        if (dynamicIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<SpaceTimelineRecord>.Filter.In(t => t.DynamicId, dynamicIds);
        var projection = Builders<SpaceTimelineRecord>.Projection.Include(t => t.DynamicId);
        var existing = await _collection.Find(filter)
            .Project<SpaceTimelineRecord>(projection)
            .ToListAsync(ct);
        return existing.Select(t => t.DynamicId).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<int> BulkUpsertAsync(
        IReadOnlyList<SpaceTimelineRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var models = new List<WriteModel<SpaceTimelineRecord>>(records.Count);
        foreach (var record in records)
        {
            var filter = Builders<SpaceTimelineRecord>.Filter.Eq(t => t.DynamicId, record.DynamicId);
            var update = Builders<SpaceTimelineRecord>.Update
                .SetOnInsert(t => t.Mid, record.Mid)
                .SetOnInsert(t => t.Data, record.Data)
                .SetOnInsert(t => t.FetchedAt, record.FetchedAt);
            models.Add(new UpdateOneModel<SpaceTimelineRecord>(filter, update) { IsUpsert = true });
        }

        var result = await _collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, ct);
        return (int)result.Upserts.Count;
    }

    public async Task<(long Total, IReadOnlyList<SpaceTimelineRecord> Items)> GetPageAsync(
        long mid, int page, int pageSize, CancellationToken ct = default)
    {
        var filter = Builders<SpaceTimelineRecord>.Filter.Eq(t => t.Mid, mid);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _collection.Find(filter)
            .SortByDescending(t => t.FetchedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }
}
