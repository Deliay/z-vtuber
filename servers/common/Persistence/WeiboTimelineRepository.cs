using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class WeiboTimelineRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<WeiboTimelineRecord> _collection =
        database.GetCollection<WeiboTimelineRecord>(MongoCollectionNames.WeiboUserTimeline);

    public async Task<HashSet<string>> GetExistingMblogIdsAsync(
        IReadOnlyCollection<string> mblogIds, CancellationToken ct = default)
    {
        if (mblogIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<WeiboTimelineRecord>.Filter.In(t => t.MblogId, mblogIds);
        var projection = Builders<WeiboTimelineRecord>.Projection.Include(t => t.MblogId);
        var existing = await _collection.Find(filter)
            .Project<WeiboTimelineRecord>(projection)
            .ToListAsync(ct);
        return existing.Select(t => t.MblogId).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<int> BulkUpsertAsync(
        IReadOnlyList<WeiboTimelineRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var models = new List<WriteModel<WeiboTimelineRecord>>(records.Count);
        foreach (var record in records)
        {
            var filter = Builders<WeiboTimelineRecord>.Filter.Eq(t => t.MblogId, record.MblogId);
            var update = Builders<WeiboTimelineRecord>.Update
                .SetOnInsert(t => t.Uid, record.Uid)
                .SetOnInsert(t => t.Data, record.Data)
                .SetOnInsert(t => t.PubTs, record.PubTs)
                .SetOnInsert(t => t.FetchedAt, record.FetchedAt);
            models.Add(new UpdateOneModel<WeiboTimelineRecord>(filter, update) { IsUpsert = true });
        }

        var result = await _collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, ct);
        return (int)result.Upserts.Count;
    }

    public async Task<(long Total, IReadOnlyList<WeiboTimelineRecord> Items)> GetPageAsync(
        long uid, int page, int pageSize, DateTime? startedAt = null, DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        var filter = BuildTimeRangeFilter(
            Builders<WeiboTimelineRecord>.Filter.Eq(t => t.Uid, uid), startedAt, endedAt);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);

        var items = await _collection.Find(filter)
            .Sort(Builders<WeiboTimelineRecord>.Sort.Descending(t => t.PubTs))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }

    internal static FilterDefinition<WeiboTimelineRecord> BuildTimeRangeFilter(
        FilterDefinition<WeiboTimelineRecord> baseFilter, DateTime? startedAt, DateTime? endedAt)
    {
        var conditions = new List<FilterDefinition<WeiboTimelineRecord>> { baseFilter };
        if (startedAt.HasValue)
        {
            conditions.Add(Builders<WeiboTimelineRecord>.Filter.Gte(t => t.PubTs, ToUnixSeconds(startedAt.Value)));
        }
        if (endedAt.HasValue)
        {
            conditions.Add(Builders<WeiboTimelineRecord>.Filter.Lte(t => t.PubTs, ToUnixSeconds(endedAt.Value)));
        }
        return conditions.Count == 1 ? conditions[0] : Builders<WeiboTimelineRecord>.Filter.And(conditions);
    }

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(utc).ToUnixTimeSeconds();
}
