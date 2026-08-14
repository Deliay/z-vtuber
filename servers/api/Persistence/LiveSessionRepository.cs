using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class LiveSessionRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveSessionRecord> _collection =
        database.GetCollection<LiveSessionRecord>(MongoCollectionNames.LiveSession);

    public async Task UpsertManyAsync(IEnumerable<LiveSessionRecord> records, CancellationToken ct = default)
    {
        var deduped = records
            .GroupBy(r => r.LiveId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        if (deduped.Count == 0)
        {
            return;
        }

        var models = new List<WriteModel<LiveSessionRecord>>(deduped.Count);
        foreach (var record in deduped)
        {
            var filter = Builders<LiveSessionRecord>.Filter.Eq(r => r.LiveId, record.LiveId);
            models.Add(new ReplaceOneModel<LiveSessionRecord>(filter, record) { IsUpsert = true });
        }

        try
        {
            await _collection.BulkWriteAsync(models, cancellationToken: ct);
        }
        catch (MongoBulkWriteException ex) when (ex.WriteErrors.All(e => e.Code == (int)ServerErrorCategory.DuplicateKey))
        {
            // A concurrent sync already persisted the same LiveId; the unique index guarantees no duplicates.
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
