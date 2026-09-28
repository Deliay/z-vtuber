using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class LiveEventTextIndexRepository(IMongoDatabase database)
{
    // 搜索索引名称，与 MongoDbInitializer 中创建的一致：
    // { RoomId: 1, OccurredAt: -1, Id: -1 }，RoomId 等值收敛 + 有序扫描，regex 作残余过滤
    public const string SearchIndexName = "RoomId_1_OccurredAt_-1_Id_-1";

    private IMongoCollection<LiveEventTextIndexRecord> Collection =>
        database.GetCollection<LiveEventTextIndexRecord>(LiveCollectionNames.LiveEventTextIndex);

    public async Task InsertAsync(LiveEventTextIndexRecord record, CancellationToken ct = default)
    {
        await Collection.InsertOneAsync(record, cancellationToken: ct);
    }

    /// <summary>
    /// 按 RoomId + Type + OccurredAt ∈ [from, to] 升序列举（上下文组装用）。
    /// 命中 { RoomId: 1, OccurredAt: -1, Id: -1 } 索引。
    /// </summary>
    public async Task<IReadOnlyList<LiveEventTextIndexRecord>> ListByRoomTypeAndWindowAsync(
        long roomId,
        string type,
        DateTime from,
        DateTime to,
        int limit,
        CancellationToken ct = default)
    {
        var filter = Builders<LiveEventTextIndexRecord>.Filter.And(
            Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.RoomId, roomId),
            Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.Type, type),
            Builders<LiveEventTextIndexRecord>.Filter.Gte(r => r.OccurredAt, from),
            Builders<LiveEventTextIndexRecord>.Filter.Lte(r => r.OccurredAt, to));

        var sort = Builders<LiveEventTextIndexRecord>.Sort.Ascending(r => r.OccurredAt);

        return await Collection.Find(filter, new FindOptions { Hint = SearchIndexName })
            .Sort(sort)
            .Limit(Math.Max(1, limit))
            .ToListAsync(ct);
    }

    public async Task<(long total, List<LiveEventTextIndexRecord> items)> SearchAsync(
        long roomId,
        string criteria,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var escapedCriteria = Regex.Escape(criteria);
        var regex = new BsonRegularExpression(escapedCriteria, "i");

        // 构建 $or 过滤器：Text/SenderName/SenderMedal regex 匹配
        var orFilters = new List<FilterDefinition<LiveEventTextIndexRecord>>
        {
            Builders<LiveEventTextIndexRecord>.Filter.Regex(r => r.Text, regex),
            Builders<LiveEventTextIndexRecord>.Filter.Regex(r => r.SenderName, regex),
            Builders<LiveEventTextIndexRecord>.Filter.Regex(r => r.SenderMedal, regex)
        };

        // 纯数字关键词追加 SenderUid 精确匹配
        if (TryParseUid(criteria, out var uid))
        {
            orFilters.Add(Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.SenderUid, uid));
        }

        var filter = Builders<LiveEventTextIndexRecord>.Filter.And(
            Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.RoomId, roomId),
            Builders<LiveEventTextIndexRecord>.Filter.Or(orFilters));

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

    internal static bool TryParseUid(string criteria, out long uid)
    {
        uid = 0;
        if (string.IsNullOrWhiteSpace(criteria))
        {
            return false;
        }

        // 检查是否全部为 ASCII 数字
        foreach (var c in criteria)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return long.TryParse(criteria, out uid);
    }
}
