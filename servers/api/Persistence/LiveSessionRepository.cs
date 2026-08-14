using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class LiveSessionRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveSessionRecord> _collection =
        database.GetCollection<LiveSessionRecord>(MongoCollectionNames.LiveSession);

    public async Task UpsertManyAsync(IEnumerable<LiveSessionRecord> records, CancellationToken ct = default)
    {
        var models = new List<WriteModel<LiveSessionRecord>>();
        foreach (var record in records)
        {
            var filter = Builders<LiveSessionRecord>.Filter.Eq(r => r.LiveId, record.LiveId);
            var upsert = new ReplaceOneModel<LiveSessionRecord>(filter, record) { IsUpsert = true };
            models.Add(upsert);
        }

        if (models.Count > 0)
        {
            await _collection.BulkWriteAsync(models, cancellationToken: ct);
        }
    }

    public async Task<IReadOnlyList<LiveSessionRecord>> GetByRoomIdAsync(long roomId, int page, int pageSize, CancellationToken ct = default)
    {
        var total = await _collection.CountDocumentsAsync(r => r.RoomId == roomId, cancellationToken: ct);
        var items = await _collection.Find(r => r.RoomId == roomId)
            .SortByDescending(r => r.StartDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return items;
    }
}
