using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class LiveSessionRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<LiveSessionRecord> _collection =
        database.GetCollection<LiveSessionRecord>(LiveCollectionNames.LiveSession);

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

        var existingFilter = Builders<LiveSessionRecord>.Filter.In(r => r.LiveId, deduped.Select(r => r.LiveId));
        var existing = await _collection.Find(existingFilter)
            .Project<LiveSessionRecord>(Builders<LiveSessionRecord>.Projection.Include(r => r.LiveId).Include(r => r.Id))
            .ToListAsync(ct);
        var idByLiveId = existing.ToDictionary(r => r.LiveId, r => r.Id, StringComparer.Ordinal);

        var models = new List<WriteModel<LiveSessionRecord>>(deduped.Count);
        foreach (var record in deduped)
        {
            record.Id = idByLiveId.TryGetValue(record.LiveId, out var id)
                ? id
                : ObjectId.GenerateNewId();

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

    public async Task<(long Total, IReadOnlyList<LiveSessionRecord> Items)> GetPageAsync(
        long roomId, int page, int pageSize, DateTime? startedAt = null, DateTime? endedAt = null,
        CancellationToken ct = default)
    {
        var filter = Builders<LiveSessionRecord>.Filter.Eq(r => r.RoomId, roomId);
        if (startedAt.HasValue)
        {
            filter &= Builders<LiveSessionRecord>.Filter.Gte(r => r.StartDate, startedAt.Value);
        }
        if (endedAt.HasValue)
        {
            filter &= Builders<LiveSessionRecord>.Filter.Lte(r => r.StartDate, endedAt.Value);
        }
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _collection.Find(filter)
            .SortByDescending(r => r.StartDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (total, items);
    }

    public async Task UpsertAsync(LiveSessionRecord record, CancellationToken ct = default)
    {
        var existing = await _collection.Find(r => r.LiveId == record.LiveId)
            .Project<LiveSessionRecord>(Builders<LiveSessionRecord>.Projection.Include(r => r.Id))
            .FirstOrDefaultAsync(ct);
        record.Id = existing?.Id ?? ObjectId.GenerateNewId();

        var filter = Builders<LiveSessionRecord>.Filter.Eq(r => r.LiveId, record.LiveId);
        try
        {
            await _collection.ReplaceOneAsync(filter, record, new ReplaceOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent write already persisted the same LiveId.
        }
    }

    public async Task CloseAsync(string liveId, DateTime stopDate, CancellationToken ct = default)
    {
        var filter = Builders<LiveSessionRecord>.Filter.And(
            Builders<LiveSessionRecord>.Filter.Eq(r => r.LiveId, liveId),
            Builders<LiveSessionRecord>.Filter.Eq(r => r.IsFinish, false));
        var update = Builders<LiveSessionRecord>.Update
            .Set(r => r.IsFinish, true)
            .Set(r => r.StopDate, stopDate);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    /// <summary>
    /// 未结束场次（StopDate==null）被认作「仍在进行」的最大时长：超过该时长仍未结束的陈旧场次
    /// 不再吞并远期文件（否则清洗会把该房间之后所有文件并进同一条 recordings）。
    /// </summary>
    public static readonly TimeSpan MaxOpenSessionDuration = TimeSpan.FromHours(48);

    /// <summary>
    /// 按 RoomId + 时间点匹配场次：返回 <c>StartDate &lt;= t &lt;= StopDate</c> 的场次；
    /// StopDate 为 null 时仅当 t 落在 [StartDate, StartDate + <see cref="MaxOpenSessionDuration"/>] 内才算命中。
    /// 窗口未命中一律返回 null（交调用方按 bilibili_live_status 兜底），不做「最近一场」宽松回退，
    /// 避免陈旧场次吞并远期文件。
    /// </summary>
    public async Task<LiveSessionRecord?> GetByRoomIdAndTimeWindowAsync(
        long roomId, DateTime t, CancellationToken ct = default)
    {
        var closedWindow = Builders<LiveSessionRecord>.Filter.Gte(r => r.StopDate, t);
        var openWindow = Builders<LiveSessionRecord>.Filter.And(
            Builders<LiveSessionRecord>.Filter.Eq(r => r.StopDate, null),
            Builders<LiveSessionRecord>.Filter.Gte(r => r.StartDate, t - MaxOpenSessionDuration));

        var filter = Builders<LiveSessionRecord>.Filter.And(
            Builders<LiveSessionRecord>.Filter.Eq(r => r.RoomId, roomId),
            Builders<LiveSessionRecord>.Filter.Lte(r => r.StartDate, t),
            Builders<LiveSessionRecord>.Filter.Or(closedWindow, openWindow));

        return await _collection.Find(filter)
            .SortByDescending(r => r.StartDate)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<LiveSessionRecord>> FindMergeCandidatesAsync(
        long roomId, DateTime startDate, TimeSpan tolerance, CancellationToken ct = default)
    {
        var lower = startDate - tolerance;
        var upper = startDate + tolerance;
        var filter = Builders<LiveSessionRecord>.Filter.And(
            Builders<LiveSessionRecord>.Filter.Eq(r => r.RoomId, roomId),
            Builders<LiveSessionRecord>.Filter.Or(
                Builders<LiveSessionRecord>.Filter.Eq(r => r.IsFinish, false),
                Builders<LiveSessionRecord>.Filter.And(
                    Builders<LiveSessionRecord>.Filter.Gte(r => r.StartDate, lower),
                    Builders<LiveSessionRecord>.Filter.Lte(r => r.StartDate, upper))));
        return await _collection.Find(filter).ToListAsync(ct);
    }
}
