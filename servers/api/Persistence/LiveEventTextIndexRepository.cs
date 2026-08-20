using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class LiveEventTextIndexRepository(IMongoDatabase database)
{
    // 搜索索引名称，与 MongoDbInitializer 中创建的一致：
    // { RoomId: 1, OccurredAt: -1, Id: -1 }，RoomId 等值收敛 + 有序扫描，regex 作残余过滤
    public const string SearchIndexName = "RoomId_1_OccurredAt_-1_Id_-1";

    private IMongoCollection<LiveEventTextIndexRecord> Collection =>
        database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);

    public async Task InsertAsync(LiveEventTextIndexRecord record, CancellationToken ct = default)
    {
        await Collection.InsertOneAsync(record, cancellationToken: ct);
    }

    public async Task<(long total, List<LiveEventTextIndexRecord> items)> SearchAsync(
        long roomId,
        string criteria,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var escapedCriteria = Regex.Escape(criteria);

        var filter = Builders<LiveEventTextIndexRecord>.Filter.And(
            Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.RoomId, roomId),
            Builders<LiveEventTextIndexRecord>.Filter.Regex(r => r.Text, new BsonRegularExpression(escapedCriteria, "i")));

        var total = await Collection.CountDocumentsAsync(filter, cancellationToken: ct);

        var sort = Builders<LiveEventTextIndexRecord>.Sort
            .Descending(r => r.OccurredAt)
            .Descending(r => r.Id);

        var items = await Collection.Find(filter, new FindOptions { Hint = SearchIndexName })
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return (total, items);
    }
}
