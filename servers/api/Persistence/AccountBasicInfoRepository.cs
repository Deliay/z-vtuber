using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class AccountBasicInfoRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<AccountBasicInfoRecord> _collection =
        database.GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);

    public Task UpsertAsync(AccountBasicInfoRecord record, CancellationToken ct = default)
    {
        var filter = Builders<AccountBasicInfoRecord>.Filter.Eq(a => a.Mid, record.Mid);
        var update = Builders<AccountBasicInfoRecord>.Update
            .Set(a => a.Mid, record.Mid)
            .Set(a => a.RoomId, record.RoomId)
            .Set(a => a.Data, record.Data)
            .Set(a => a.FetchedAt, record.FetchedAt);
        return _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public Task<AccountBasicInfoRecord> GetLatestByMidAsync(long mid, CancellationToken ct = default) =>
        _collection.Find(a => a.Mid == mid)
            .SortByDescending(a => a.FetchedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<long?> GetLatestRoomIdByMidAsync(long mid, CancellationToken ct = default)
    {
        var projection = Builders<AccountBasicInfoRecord>.Projection.Include(a => a.RoomId);
        var latest = await _collection.Find(a => a.Mid == mid)
            .SortByDescending(a => a.FetchedAt)
            .Project<AccountBasicInfoRecord>(projection)
            .FirstOrDefaultAsync(ct);
        return latest?.RoomId;
    }

    public async Task<Dictionary<long, AccountBasicInfoRecord>> GetLatestByMidsAsync(
        IReadOnlyCollection<long> mids, CancellationToken ct = default)
    {
        if (mids.Count == 0)
        {
            return new Dictionary<long, AccountBasicInfoRecord>();
        }

        var filter = Builders<AccountBasicInfoRecord>.Filter.In(a => a.Mid, mids);
        var records = await _collection.Find(filter).ToListAsync(ct);

        return records
            .GroupBy(a => a.Mid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.FetchedAt).First());
    }
}
