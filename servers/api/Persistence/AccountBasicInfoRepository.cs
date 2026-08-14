using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class AccountBasicInfoRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<AccountBasicInfoRecord> _collection =
        database.GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);

    public Task InsertAsync(AccountBasicInfoRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

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
}
