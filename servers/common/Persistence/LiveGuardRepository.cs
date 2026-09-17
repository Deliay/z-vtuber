using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class LiveGuardRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveGuardRecord> _collection =
        database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);

    public Task UpsertDailySnapshotAsync(LiveGuardRecord record, CancellationToken ct = default)
    {
        var filter = Builders<LiveGuardRecord>.Filter.And(
            Builders<LiveGuardRecord>.Filter.Eq(r => r.Mid, record.Mid),
            Builders<LiveGuardRecord>.Filter.Eq(r => r.Date, record.Date));
        var update = Builders<LiveGuardRecord>.Update
            .Set(r => r.Mid, record.Mid)
            .Set(r => r.RoomId, record.RoomId)
            .Set(r => r.Date, record.Date)
            .Set(r => r.Guards, record.Guards)
            .Set(r => r.GuardCount, record.GuardCount)
            .Set(r => r.FetchedAt, record.FetchedAt);
        return _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<IReadOnlyList<long>> GetMidsWithSnapshotAsync(string date, CancellationToken ct = default)
    {
        var filter = Builders<LiveGuardRecord>.Filter.Eq(r => r.Date, date);
        var projection = Builders<LiveGuardRecord>.Projection.Include(r => r.Mid);
        var records = await _collection.Find(filter)
            .Project<LiveGuardRecord>(projection)
            .ToListAsync(ct);
        return records.Select(r => r.Mid).ToList();
    }

    public async Task<IReadOnlyList<LiveGuardEntry>> GetOnboardGuardEntriesAsync(
        long roomId,
        IReadOnlyList<string> dates,
        bool requireAllDays,
        CancellationToken ct = default)
    {
        if (dates.Count == 0)
        {
            return [];
        }

        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "RoomId", roomId },
                { "Date", new BsonDocument("$in", new BsonArray(dates)) },
            }),
            new BsonDocument("$unwind", "$Guards"),
            new BsonDocument("$sort", new BsonDocument("Date", -1)),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$Guards.Uid" },
                { "days", new BsonDocument("$addToSet", "$Date") },
                { "latest", new BsonDocument("$first", "$Guards") },
            }),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };

        var result = await _collection.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        var items = result.Select(doc => new
        {
            Entry = new LiveGuardEntry
            {
                Uid = doc["_id"].AsInt64,
                UserName = doc["latest"]["UserName"].AsString,
                GuardLevel = doc["latest"]["GuardLevel"].AsInt32,
                MedalName = doc["latest"].AsBsonDocument.Contains("MedalName") && !doc["latest"]["MedalName"].IsBsonNull
                    ? doc["latest"]["MedalName"].AsString
                    : null,
                MedalLevel = doc["latest"]["MedalLevel"].AsInt32,
            },
            DaysCount = doc["days"].AsBsonArray.Count,
        });

        if (requireAllDays)
        {
            items = items.Where(e => e.DaysCount == dates.Count);
        }

        return items.Select(e => e.Entry).ToList();
    }

    /// <summary>按 RoomId 取最新一天（Date 最大）的舰长快照；无快照返回 null。命中 RoomId_1_Date_1 索引。</summary>
    public async Task<LiveGuardRecord?> GetLatestSnapshotByRoomIdAsync(long roomId, CancellationToken ct = default)
    {
        var filter = Builders<LiveGuardRecord>.Filter.Eq(r => r.RoomId, roomId);
        var sort = Builders<LiveGuardRecord>.Sort.Descending(r => r.Date);
        return await _collection.Find(filter).Sort(sort).Limit(1).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<long>> GetDistinctMidsAsync(CancellationToken ct = default)
    {
        var filter = Builders<LiveGuardRecord>.Filter.Empty;
        var distinctValues = await _collection.DistinctAsync(r => r.Mid, filter, cancellationToken: ct);
        return await distinctValues.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LiveGuardRecord>> GetSnapshotsByMidAsync(
        long mid,
        string beforeDateExclusive,
        CancellationToken ct = default)
    {
        var filter = Builders<LiveGuardRecord>.Filter.And(
            Builders<LiveGuardRecord>.Filter.Eq(r => r.Mid, mid),
            Builders<LiveGuardRecord>.Filter.Lt(r => r.Date, beforeDateExclusive));
        var sort = Builders<LiveGuardRecord>.Sort.Ascending(r => r.Date);
        return await _collection.Find(filter)
            .Sort(sort)
            .ToListAsync(ct);
    }

    public async Task BackfillMissingEntriesAsync(
        long mid,
        string date,
        IReadOnlyList<LiveGuardEntry> missing,
        int finalGuardCount,
        CancellationToken ct = default)
    {
        if (missing.Count == 0)
        {
            return;
        }

        var filter = Builders<LiveGuardRecord>.Filter.And(
            Builders<LiveGuardRecord>.Filter.Eq(r => r.Mid, mid),
            Builders<LiveGuardRecord>.Filter.Eq(r => r.Date, date));
        var update = Builders<LiveGuardRecord>.Update
            .PushEach(r => r.Guards, missing)
            .Set(r => r.GuardCount, finalGuardCount);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
