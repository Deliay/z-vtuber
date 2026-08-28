using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class CookieAccountInfoRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<CookieAccountInfoRecord> _collection =
        database.GetCollection<CookieAccountInfoRecord>(MongoCollectionNames.CookieAccountInfo);

    public Task UpsertAsync(CookieAccountInfoRecord record, CancellationToken ct = default)
    {
        if (record.Id == ObjectId.Empty)
        {
            record.Id = ObjectId.GenerateNewId();
        }

        var filter = Builders<CookieAccountInfoRecord>.Filter.Eq(r => r.CookieId, record.CookieId);
        var update = Builders<CookieAccountInfoRecord>.Update
            .SetOnInsert(r => r.Id, record.Id)
            .Set(r => r.Mid, record.Mid)
            .Set(r => r.Uname, record.Uname)
            .Set(r => r.FetchedAt, record.FetchedAt);
        return _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<Dictionary<ObjectId, CookieAccountInfoRecord>> GetByCookieIdsAsync(
        IReadOnlyCollection<ObjectId> cookieIds, CancellationToken ct = default)
    {
        if (cookieIds.Count == 0)
        {
            return new Dictionary<ObjectId, CookieAccountInfoRecord>();
        }

        var filter = Builders<CookieAccountInfoRecord>.Filter.In(r => r.CookieId, cookieIds);
        var records = await _collection.Find(filter).ToListAsync(ct);
        return records.ToDictionary(r => r.CookieId, r => r);
    }
}