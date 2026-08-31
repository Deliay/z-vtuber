using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

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
}