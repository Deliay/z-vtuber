using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

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
                .SetOnInsert(t => t.PubTs, record.PubTs)
                .SetOnInsert(t => t.FetchedAt, record.FetchedAt);
            models.Add(new UpdateOneModel<SpaceTimelineRecord>(filter, update) { IsUpsert = true });
        }

        var result = await _collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, ct);
        return (int)result.Upserts.Count;
    }

    public async Task<(long Total, IReadOnlyList<SpaceTimelineRecord> Items)> GetPageAsync(
        long mid, int page, int pageSize, DateTime? startedAt = null, DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        var filter = BuildTimeRangeFilter(
            Builders<SpaceTimelineRecord>.Filter.Eq(t => t.Mid, mid), startedAt, endedAt);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);

        var numericIdStage = BsonDocument.Parse(
            """{ $addFields: { _dynamicIdLong: { $convert: { input: "$DynamicId", to: "long", onError: null, onNull: null } } } }""");
        var unsetStage = BsonDocument.Parse("""{ $unset: "_dynamicIdLong" }""");

        var items = await _collection.Aggregate()
            .Match(filter)
            .AppendStage<SpaceTimelineRecord>(numericIdStage)
            .Sort(Builders<SpaceTimelineRecord>.Sort.Descending("_dynamicIdLong"))
            .AppendStage<SpaceTimelineRecord>(unsetStage)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }

    internal static FilterDefinition<SpaceTimelineRecord> BuildTimeRangeFilter(
        FilterDefinition<SpaceTimelineRecord> baseFilter, DateTime? startedAt, DateTime? endedAt)
    {
        var conditions = new List<FilterDefinition<SpaceTimelineRecord>> { baseFilter };
        if (startedAt.HasValue)
        {
            conditions.Add(Builders<SpaceTimelineRecord>.Filter.Gte(t => t.PubTs, ToUnixSeconds(startedAt.Value)));
        }
        if (endedAt.HasValue)
        {
            conditions.Add(Builders<SpaceTimelineRecord>.Filter.Lte(t => t.PubTs, ToUnixSeconds(endedAt.Value)));
        }
        return conditions.Count == 1 ? conditions[0] : Builders<SpaceTimelineRecord>.Filter.And(conditions);
    }

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(utc).ToUnixTimeSeconds();
}
