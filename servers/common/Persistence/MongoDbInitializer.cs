using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class MongoCollectionNames
{
    public const string Cookies = "bilibili_cookies";
    public const string CookieSessions = "bilibili_cookie_sessions";
    public const string Subscriptions = "bilibili_subscriptions";
    public const string AccountBasicInfo = "bilibili_account_basic_info";
    public const string LiveStatus = "bilibili_live_status";
    public const string LiveEvent = "bilibili_live_event";
    public const string SpaceTimeline = "bilibili_user_space_timeline";
    public const string LiveSession = "bilibili_live_session";
    public const string Meta = "zvtuber_meta";
    public const string LiveEventTextIndex = "bilibili_live_event_text_index";
    public const string CookieAccountInfo = "bilibili_cookie_account_info";
    public const string LiveGuards = "bilibili_live_guards";
    public const string WeiboSubscriptions = "weibo_subscriptions";
    public const string WeiboUserTimeline = "weibo_user_timeline";
    public const string WeiboCookies = "weibo_cookies";
    public const string WeiboCookieSessions = "weibo_cookie_sessions";
    public const string NotificationSubscriptions = "notification_subscriptions";
}

public sealed class MongoDbInitializer(IMongoDatabase database, ILogger<MongoDbInitializer> logger)
{
    public const string DatabaseName = "zvtuber";

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await EnsureCookiesIndexesAsync(ct);
        await EnsureCookieSessionIndexesAsync(ct);
        await EnsureSubscriptionsIndexesAsync(ct);
        await EnsureAccountBasicInfoIndexesAsync(ct);
        await EnsureLiveStatusIndexesAsync(ct);
        await EnsureLiveEventIndexesAsync(ct);
        await EnsureSpaceTimelineIndexesAsync(ct);
        await EnsureLiveSessionIndexesAsync(ct);
        await EnsureLiveEventTextIndexAsync(ct);
        await EnsureCookieAccountInfoIndexesAsync(ct);
        await EnsureLiveGuardIndexesAsync(ct);
        await EnsureLiveGuardRoomDateIndexAsync(ct);
        await EnsureWeiboSubscriptionIndexesAsync(ct);
        await EnsureWeiboTimelineIndexesAsync(ct);
        await EnsureWeiboCookieIndexesAsync(ct);
        await EnsureNotificationSubscriptionIndexesAsync(ct);
        logger.LogInformation("MongoDB indexes ensured for database {Database}", DatabaseName);
    }

    public async Task DropOldSubscriptionIndexAsync(CancellationToken ct = default)
    {
        var collection = database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await DropIndexIfExistsAsync(collection, "Mid_1", ct);
        logger.LogInformation("Dropped old Mid_1 subscription index if it existed.");
    }

    public async Task EnsureSubscriptionCompositeIndexAsync(CancellationToken ct = default)
    {
        var collection = database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        var keys = Builders<SubscriptionRecord>.IndexKeys
            .Ascending(s => s.Mid)
            .Ascending(s => s.Type);
        var options = new CreateIndexOptions { Unique = true, Name = "Mid_1_Type_1" };
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<SubscriptionRecord>(keys, options), cancellationToken: ct);
        logger.LogInformation("Ensured (Mid, Type) composite unique index on subscriptions.");
    }

    private async Task EnsureCookiesIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<CookieRecord>(MongoCollectionNames.Cookies);
        var keys = Builders<CookieRecord>.IndexKeys
            .Ascending(c => c.Status)
            .Ascending(c => c.LastUsedAt);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<CookieRecord>(keys), cancellationToken: ct);

        var accountIdKeys = Builders<CookieRecord>.IndexKeys.Ascending(c => c.AccountId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieRecord>(accountIdKeys, new CreateIndexOptions { Name = "AccountId_1" }),
            cancellationToken: ct);
    }

    private async Task EnsureCookieSessionIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<CookieSessionRecord>(MongoCollectionNames.CookieSessions);
        var statusKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.Status);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(statusKeys, new CreateIndexOptions { Name = "Status_1" }),
            cancellationToken: ct);

        var cookieIdKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.CookieId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(cookieIdKeys, new CreateIndexOptions { Name = "CookieId_1" }),
            cancellationToken: ct);
    }

    private async Task EnsureSubscriptionsIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);

        // 删除旧的 Mid 单字段唯一索引（如果存在）
        await DropIndexIfExistsAsync(collection, "Mid_1", ct);

        // 创建 (Mid, Type) 复合唯一索引
        var keys = Builders<SubscriptionRecord>.IndexKeys
            .Ascending(s => s.Mid)
            .Ascending(s => s.Type);
        var options = new CreateIndexOptions { Unique = true, Name = "Mid_1_Type_1" };
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<SubscriptionRecord>(keys, options), cancellationToken: ct);
    }

    private Task EnsureAccountBasicInfoIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        var keys = Builders<AccountBasicInfoRecord>.IndexKeys
            .Ascending(a => a.Mid)
            .Descending(a => a.FetchedAt);
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<AccountBasicInfoRecord>(keys), cancellationToken: ct);
    }

    private Task EnsureLiveStatusIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveStatusRecord>(MongoCollectionNames.LiveStatus);
        var keys = Builders<LiveStatusRecord>.IndexKeys
            .Ascending(l => l.RoomId)
            .Descending(l => l.CreatedAt);
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveStatusRecord>(keys), cancellationToken: ct);
    }

    private async Task EnsureLiveEventIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);

        var roomTimeKeys = Builders<LiveEventRecord>.IndexKeys
            .Ascending(e => e.RoomId)
            .Ascending(e => e.OccurredAt);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventRecord>(roomTimeKeys), cancellationToken: ct);

        var roomTypeTimeKeys = Builders<LiveEventRecord>.IndexKeys
            .Ascending(e => e.RoomId)
            .Ascending(e => e.Type)
            .Descending(e => e.OccurredAt);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventRecord>(roomTypeTimeKeys), cancellationToken: ct);

        var roomTypePriceTimeKeys = Builders<LiveEventRecord>.IndexKeys
            .Ascending(e => e.RoomId)
            .Ascending(e => e.Type)
            .Ascending(e => e.Price)
            .Descending(e => e.OccurredAt);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventRecord>(roomTypePriceTimeKeys), cancellationToken: ct);

        // 多 type 查询复合索引：RoomId asc + OccurredAt desc + Type asc（显式命名，供查询 hint 引用）
        var roomOccurredAtTypeKeys = Builders<LiveEventRecord>.IndexKeys
            .Ascending(e => e.RoomId)
            .Descending(e => e.OccurredAt)
            .Ascending(e => e.Type);
        var roomOccurredAtTypeOptions = new CreateIndexOptions { Name = "RoomId_1_OccurredAt_-1_Type_1" };
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<LiveEventRecord>(roomOccurredAtTypeKeys, roomOccurredAtTypeOptions),
            cancellationToken: ct);

        // 清理旧版索引：72cab58 误将排序回退为升序时创建的升序索引（降序为 6e23b69 显式需求）
        await DropIndexIfExistsAsync(collection, "RoomId_1_Type_1_OccurredAt_1", ct);
    }

    private async Task EnsureLiveEventTextIndexAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);

        // 创建 { RoomId: 1, OccurredAt: -1, Id: -1 } 复合索引：RoomId 等值收敛后按排序键顺序扫描，
        // regex 子串匹配作残余过滤，skip/limit 可提前终止，避免内存排序（32MB 上限风险）
        var searchIndexKeys = Builders<LiveEventTextIndexRecord>.IndexKeys
            .Ascending(r => r.RoomId)
            .Descending(r => r.OccurredAt)
            .Descending(r => r.Id);
        var searchIndexOptions = new CreateIndexOptions { Name = LiveEventTextIndexRepository.SearchIndexName };
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<LiveEventTextIndexRecord>(searchIndexKeys, searchIndexOptions),
            cancellationToken: ct);

        // 创建 EventId asc 唯一索引
        var eventIdKeys = Builders<LiveEventTextIndexRecord>.IndexKeys.Ascending(r => r.EventId);
        var eventIdOptions = new CreateIndexOptions { Unique = true };
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<LiveEventTextIndexRecord>(eventIdKeys, eventIdOptions),
            cancellationToken: ct);

        // 清理旧版索引：$text 方案的全文索引，以及无法服务 regex 与排序的普通 Text 索引
        await DropIndexIfExistsAsync(collection, "RoomId_Text_text", ct);
        await DropIndexIfExistsAsync(collection, "RoomId_Text_1", ct);
    }

    private Task EnsureCookieAccountInfoIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<CookieAccountInfoRecord>(MongoCollectionNames.CookieAccountInfo);
        var keys = Builders<CookieAccountInfoRecord>.IndexKeys.Ascending(r => r.CookieId);
        var options = new CreateIndexOptions { Unique = true, Name = "CookieId_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<CookieAccountInfoRecord>(keys, options), cancellationToken: ct);
    }

    private Task EnsureLiveGuardIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var keys = Builders<LiveGuardRecord>.IndexKeys
            .Ascending(r => r.Mid)
            .Ascending(r => r.Date);
        var options = new CreateIndexOptions { Unique = true, Name = "Mid_1_Date_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveGuardRecord>(keys, options), cancellationToken: ct);
    }

    private Task EnsureLiveGuardRoomDateIndexAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveGuardRecord>(MongoCollectionNames.LiveGuards);
        var keys = Builders<LiveGuardRecord>.IndexKeys
            .Ascending(r => r.RoomId)
            .Ascending(r => r.Date);
        var options = new CreateIndexOptions { Name = "RoomId_1_Date_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveGuardRecord>(keys, options), cancellationToken: ct);
    }

    private static async Task DropIndexIfExistsAsync<T>(IMongoCollection<T> collection, string name, CancellationToken ct)
    {
        using var cursor = await collection.Indexes.ListAsync(ct);
        var indexes = await cursor.ToListAsync(ct);
        if (indexes.Any(i => i["name"].AsString == name))
        {
            await collection.Indexes.DropOneAsync(name, ct);
        }
    }

    private async Task EnsureSpaceTimelineIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        var uniqueKeys = Builders<SpaceTimelineRecord>.IndexKeys.Ascending(t => t.DynamicId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<SpaceTimelineRecord>(uniqueKeys, new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);

        var midKeys = Builders<SpaceTimelineRecord>.IndexKeys
            .Ascending(t => t.Mid)
            .Descending(t => t.FetchedAt);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<SpaceTimelineRecord>(midKeys), cancellationToken: ct);

        var midDynamicIdKeys = Builders<SpaceTimelineRecord>.IndexKeys
            .Ascending(t => t.Mid)
            .Descending(t => t.DynamicId);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<SpaceTimelineRecord>(midDynamicIdKeys), cancellationToken: ct);

        var midPubTsKeys = Builders<SpaceTimelineRecord>.IndexKeys
            .Ascending(t => t.Mid)
            .Descending(t => t.PubTs);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<SpaceTimelineRecord>(midPubTsKeys), cancellationToken: ct);
    }

    private async Task EnsureLiveSessionIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveSessionRecord>(MongoCollectionNames.LiveSession);
        var uniqueKeys = Builders<LiveSessionRecord>.IndexKeys.Ascending(l => l.LiveId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<LiveSessionRecord>(uniqueKeys, new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);

        var roomKeys = Builders<LiveSessionRecord>.IndexKeys
            .Ascending(l => l.RoomId)
            .Descending(l => l.StartDate);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveSessionRecord>(roomKeys), cancellationToken: ct);
    }

    private Task EnsureWeiboSubscriptionIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<WeiboSubscriptionRecord>(MongoCollectionNames.WeiboSubscriptions);
        var keys = Builders<WeiboSubscriptionRecord>.IndexKeys.Ascending(s => s.Uid);
        var options = new CreateIndexOptions { Unique = true, Name = "Uid_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<WeiboSubscriptionRecord>(keys, options), cancellationToken: ct);
    }

    private async Task EnsureWeiboTimelineIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<WeiboTimelineRecord>(MongoCollectionNames.WeiboUserTimeline);

        var mblogIdKeys = Builders<WeiboTimelineRecord>.IndexKeys.Ascending(t => t.MblogId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<WeiboTimelineRecord>(mblogIdKeys, new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);

        var uidPubTsKeys = Builders<WeiboTimelineRecord>.IndexKeys
            .Ascending(t => t.Uid)
            .Descending(t => t.PubTs);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<WeiboTimelineRecord>(uidPubTsKeys), cancellationToken: ct);

        var uidMblogIdKeys = Builders<WeiboTimelineRecord>.IndexKeys
            .Ascending(t => t.Uid)
            .Descending(t => t.MblogId);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<WeiboTimelineRecord>(uidMblogIdKeys), cancellationToken: ct);
    }

    public const string SpaceTimelinePubTsMigrationKey = "space_timeline_pub_ts_backfill_v1";

    public async Task BackfillSpaceTimelinePubTsAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == SpaceTimelinePubTsMigrationKey)
            .AnyAsync(ct);
        if (markerExists)
        {
            logger.LogInformation("Space timeline PubTs backfill already applied; skipping.");
            return;
        }

        const int batchSize = 500;
        var collection = database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);

        long processed = batchSize;
        do
        {
            var cursor = await collection.Find(r => r.PubTs == null)
                .Project<SpaceTimelineRecord>(Builders<SpaceTimelineRecord>.Projection
                    .Include(r => r.Id)
                    .Include(r => r.Data))
                .Limit(batchSize)
                .ToCursorAsync(ct);

            var batch = await cursor.ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            var models = new List<WriteModel<SpaceTimelineRecord>>(batch.Count);
            foreach (var record in batch)
            {
                var pubTs = Services.PubTsExtractor.FromRawJson(record.Data);
                if (pubTs is null)
                {
                    continue;
                }

                models.Add(new UpdateOneModel<SpaceTimelineRecord>(
                    Builders<SpaceTimelineRecord>.Filter.Eq(r => r.Id, record.Id),
                    Builders<SpaceTimelineRecord>.Update.Set(r => r.PubTs, pubTs)));
            }

            if (models.Count == 0)
            {
                logger.LogInformation(
                    "Backfill stopped: {Skipped} records in batch had no extractable PubTs.", batch.Count);
                break;
            }

            await collection.BulkWriteAsync(models, cancellationToken: ct);
            logger.LogInformation("Backfilled PubTs for {Count} space timeline records.", models.Count);
            processed = batch.Count;
        }
        while (processed == batchSize);

        await MarkMigrationAppliedAsync(SpaceTimelinePubTsMigrationKey, ct);
        logger.LogInformation("Space timeline PubTs backfill completed and marked as applied.");
    }

    public const string GiftPriceBackfillMigrationKey = "live_event_gift_price_backfill_v2";

    public async Task BackfillGiftPriceAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == GiftPriceBackfillMigrationKey)
            .AnyAsync(ct);
        if (markerExists)
        {
            logger.LogInformation("Gift price backfill already applied; skipping.");
            return;
        }

        const int batchSize = 500;
        var collection = database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);

        // 仅匹配 Price 字段不存在的 gift 记录（Price==0 已在 v1/v2 迁移中处理）
        var typeFilter = Builders<LiveEventRecord>.Filter.Eq(e => e.Type, "gift");
        var noPriceFilter = Builders<LiveEventRecord>.Filter.Not(Builders<LiveEventRecord>.Filter.Exists(e => e.Price));

        MongoDB.Bson.ObjectId lastId = MongoDB.Bson.ObjectId.Empty;
        long totalProcessed = 0;

        while (true)
        {
            var idFilter = Builders<LiveEventRecord>.Filter.Gt(e => e.Id, lastId);
            var combinedFilter = Builders<LiveEventRecord>.Filter.And(typeFilter, noPriceFilter, idFilter);

            var batch = await collection.Find(combinedFilter)
                .Project<LiveEventRecord>(Builders<LiveEventRecord>.Projection
                    .Include(r => r.Id)
                    .Include(r => r.Type)
                    .Include(r => r.Payload))
                .SortBy(e => e.Id)
                .Limit(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var models = new List<WriteModel<LiveEventRecord>>(batch.Count);
            foreach (var record in batch)
            {
                var payloadString = record.Payload.ToJson();
                var price = Live.GiftPriceExtractor.ExtractGoldSeeds(record.Type, payloadString);
                models.Add(new UpdateOneModel<LiveEventRecord>(
                    Builders<LiveEventRecord>.Filter.Eq(r => r.Id, record.Id),
                    Builders<LiveEventRecord>.Update.Set(r => r.Price, price)));
            }

            await collection.BulkWriteAsync(models, cancellationToken: ct);
            logger.LogInformation("Backfilled gift price for {Count} records.", models.Count);

            lastId = batch[^1].Id;
            totalProcessed += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        logger.LogInformation("Gift price backfill total processed {Total} records.", totalProcessed);

        await MarkMigrationAppliedAsync(GiftPriceBackfillMigrationKey, ct);
        logger.LogInformation("Gift price backfill completed and marked as applied.");
    }

    public const string LiveEventTextIndexBackfillMigrationKey = "live_event_text_index_backfill_v1";

    public async Task BackfillLiveEventTextIndexAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == LiveEventTextIndexBackfillMigrationKey)
            .AnyAsync(ct);
        if (markerExists)
        {
            logger.LogInformation("Live event text index backfill already applied; skipping.");
            return;
        }

        const int batchSize = 500;
        var eventCollection = database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var textIndexCollection = database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);

        // 筛选弹幕和醒目留言事件
        var typeFilter = Builders<LiveEventRecord>.Filter.In(e => e.Type, new[] { "danmaku", "super_chat" });

        // 使用基于 _id 的游标分页，避免死循环
        MongoDB.Bson.ObjectId lastId = MongoDB.Bson.ObjectId.Empty;
        long totalProcessed = 0;

        while (true)
        {
            var idFilter = Builders<LiveEventRecord>.Filter.Gt(e => e.Id, lastId);
            var combinedFilter = Builders<LiveEventRecord>.Filter.And(typeFilter, idFilter);

            var batch = await eventCollection.Find(combinedFilter)
                .Project<LiveEventRecord>(Builders<LiveEventRecord>.Projection
                    .Include(r => r.Id)
                    .Include(r => r.Type)
                    .Include(r => r.Payload)
                    .Include(r => r.RoomId)
                    .Include(r => r.OccurredAt))
                .SortBy(e => e.Id)
                .Limit(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            // 批量查询现有记录以保留 _id（避免 immutable _id 错误）
            var eventIds = batch.Select(r => r.Id).ToList();
            var existingRecords = await textIndexCollection
                .Find(r => eventIds.Contains(r.EventId))
                .ToListAsync(ct);
            var existingDict = existingRecords.ToDictionary(r => r.EventId, r => r.Id);

            var models = new List<WriteModel<LiveEventTextIndexRecord>>(batch.Count);
            foreach (var record in batch)
            {
                var payloadString = record.Payload.ToJson();
                var extraction = Live.LiveEventTextExtractor.Extract(record.Type, payloadString);
                if (extraction is null)
                {
                    continue;
                }

                var textIndexRecord = new LiveEventTextIndexRecord
                {
                    Id = existingDict.TryGetValue(record.Id, out var existingId) ? existingId : MongoDB.Bson.ObjectId.GenerateNewId(),
                    EventId = record.Id,
                    RoomId = record.RoomId,
                    Type = record.Type,
                    Text = extraction.Text,
                    SenderName = extraction.SenderName,
                    SenderUid = extraction.SenderUid,
                    SenderMedal = extraction.SenderMedal,
                    OccurredAt = record.OccurredAt
                };

                // 使用 upsert，唯一索引兜底幂等
                var upsert = new ReplaceOneModel<LiveEventTextIndexRecord>(
                    Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.EventId, record.Id),
                    textIndexRecord)
                {
                    IsUpsert = true
                };
                models.Add(upsert);
            }

            if (models.Count > 0)
            {
                await textIndexCollection.BulkWriteAsync(models, cancellationToken: ct);
                logger.LogInformation("Backfilled live event text index for {Count} records.", models.Count);
            }

            lastId = batch[^1].Id;
            totalProcessed += batch.Count;

            // 如果本批不足 batchSize，说明已到末尾
            if (batch.Count < batchSize)
            {
                break;
            }
        }

        logger.LogInformation("Backfill total processed {Total} events.", totalProcessed);

        await MarkMigrationAppliedAsync(LiveEventTextIndexBackfillMigrationKey, ct);
        logger.LogInformation("Live event text index backfill completed and marked as applied.");
    }

    public const string LiveEventSenderBackfillMigrationKey = "live_event_text_index_sender_backfill_v1";

    public async Task BackfillLiveEventSenderAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == LiveEventSenderBackfillMigrationKey)
            .AnyAsync(ct);
        if (markerExists)
        {
            logger.LogInformation("Live event sender backfill already applied; skipping.");
            return;
        }

        const int batchSize = 500;
        var eventCollection = database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var textIndexCollection = database.GetCollection<LiveEventTextIndexRecord>(MongoCollectionNames.LiveEventTextIndex);

        // 筛选弹幕和醒目留言事件
        var typeFilter = Builders<LiveEventRecord>.Filter.In(e => e.Type, new[] { "danmaku", "super_chat" });

        // 使用基于 _id 的游标分页，避免死循环
        MongoDB.Bson.ObjectId lastId = MongoDB.Bson.ObjectId.Empty;
        long totalProcessed = 0;

        while (true)
        {
            var idFilter = Builders<LiveEventRecord>.Filter.Gt(e => e.Id, lastId);
            var combinedFilter = Builders<LiveEventRecord>.Filter.And(typeFilter, idFilter);

            var batch = await eventCollection.Find(combinedFilter)
                .Project<LiveEventRecord>(Builders<LiveEventRecord>.Projection
                    .Include(r => r.Id)
                    .Include(r => r.Type)
                    .Include(r => r.Payload)
                    .Include(r => r.RoomId)
                    .Include(r => r.OccurredAt))
                .SortBy(e => e.Id)
                .Limit(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            // 批量查询现有记录以保留 _id（消除 N+1 查询）
            var eventIds = batch.Select(r => r.Id).ToList();
            var existingRecords = await textIndexCollection
                .Find(r => eventIds.Contains(r.EventId))
                .ToListAsync(ct);
            var existingDict = existingRecords.ToDictionary(r => r.EventId, r => r.Id);

            var models = new List<WriteModel<LiveEventTextIndexRecord>>(batch.Count);
            foreach (var record in batch)
            {
                var payloadString = record.Payload.ToJson();
                var extraction = Live.LiveEventTextExtractor.Extract(record.Type, payloadString);
                if (extraction is null)
                {
                    continue;
                }

                var textIndexRecord = new LiveEventTextIndexRecord
                {
                    Id = existingDict.TryGetValue(record.Id, out var existingId) ? existingId : MongoDB.Bson.ObjectId.GenerateNewId(),
                    EventId = record.Id,
                    RoomId = record.RoomId,
                    Type = record.Type,
                    Text = extraction.Text,
                    SenderName = extraction.SenderName,
                    SenderUid = extraction.SenderUid,
                    SenderMedal = extraction.SenderMedal,
                    OccurredAt = record.OccurredAt
                };

                // 使用 upsert，唯一索引兜底幂等
                var upsert = new ReplaceOneModel<LiveEventTextIndexRecord>(
                    Builders<LiveEventTextIndexRecord>.Filter.Eq(r => r.EventId, record.Id),
                    textIndexRecord)
                {
                    IsUpsert = true
                };
                models.Add(upsert);
            }

            if (models.Count > 0)
            {
                await textIndexCollection.BulkWriteAsync(models, cancellationToken: ct);
                logger.LogInformation("Backfilled live event sender for {Count} records.", models.Count);
            }

            lastId = batch[^1].Id;
            totalProcessed += batch.Count;

            // 如果本批不足 batchSize，说明已到末尾
            if (batch.Count < batchSize)
            {
                break;
            }
        }

        logger.LogInformation("Live event sender backfill total processed {Total} events.", totalProcessed);

        await MarkMigrationAppliedAsync(LiveEventSenderBackfillMigrationKey, ct);
        logger.LogInformation("Live event sender backfill completed and marked as applied.");
    }

    // 使用 upsert 避免重复键错误：api 与 crawler 双服务并发启动初始化时，marker 可能已被另一方写入
    private async Task MarkMigrationAppliedAsync(string key, CancellationToken ct)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var filter = Builders<MetaRecord>.Filter.Eq(r => r.Key, key);
        await meta.ReplaceOneAsync(filter, new MetaRecord { Key = key }, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public const string SubscriptionTypeSplitMigrationKey = "subscription_type_split_v1";
    public const string CookieInvalidEventTypeSplitMigrationKey = "cookie_invalid_platform_split_v1";

    public async Task MigrateSubscriptionTypeSplitAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == SubscriptionTypeSplitMigrationKey)
            .AnyAsync(ct);

        // 即使 marker 存在，也需要检查是否有未迁移的记录（回滚场景）
        var collection = database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        var noTypeFilter = Builders<SubscriptionRecord>.Filter.Not(Builders<SubscriptionRecord>.Filter.Exists(s => s.Type));
        var hasUnmigrated = await collection.Find(noTypeFilter).AnyAsync(ct);

        if (markerExists && !hasUnmigrated)
        {
            logger.LogInformation("Subscription type split migration already applied; skipping.");
            return;
        }

        const int batchSize = 500;

        long totalProcessed = 0;

        while (true)
        {
            var batch = await collection.Find(noTypeFilter)
                .Limit(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            // 批量查询已存在的 danmaku 记录以复用 _id（避免 immutable _id 错误）
            var mids = batch.Select(r => r.Mid).ToList();
            var existingDanmakuFilter = Builders<SubscriptionRecord>.Filter.And(
                Builders<SubscriptionRecord>.Filter.In(s => s.Mid, mids),
                Builders<SubscriptionRecord>.Filter.Eq(s => s.Type, SubscriptionType.Danmaku));
            var existingDanmakuRecords = await collection.Find(existingDanmakuFilter).ToListAsync(ct);
            var existingDanmakuDict = existingDanmakuRecords.ToDictionary(r => r.Mid, r => r.Id);

            var models = new List<WriteModel<SubscriptionRecord>>(batch.Count);
            foreach (var record in batch)
            {
                // 1. 更新原记录为 dynamic 类型
                models.Add(new UpdateOneModel<SubscriptionRecord>(
                    Builders<SubscriptionRecord>.Filter.Eq(s => s.Id, record.Id),
                    Builders<SubscriptionRecord>.Update.Set(s => s.Type, SubscriptionType.Dynamic)));

                // 2. 插入或更新 danmaku 类型的第二条记录（使用 upsert 避免崩溃缝隙）
                var danmakuFilter = Builders<SubscriptionRecord>.Filter.And(
                    Builders<SubscriptionRecord>.Filter.Eq(s => s.Mid, record.Mid),
                    Builders<SubscriptionRecord>.Filter.Eq(s => s.Type, SubscriptionType.Danmaku));
                models.Add(new ReplaceOneModel<SubscriptionRecord>(
                    danmakuFilter,
                    new SubscriptionRecord
                    {
                        Id = existingDanmakuDict.TryGetValue(record.Mid, out var existingId) ? existingId : MongoDB.Bson.ObjectId.GenerateNewId(),
                        Mid = record.Mid,
                        Type = SubscriptionType.Danmaku,
                        SubscribedAt = record.SubscribedAt
                    }) { IsUpsert = true });
            }

            await collection.BulkWriteAsync(models, cancellationToken: ct);
            logger.LogInformation("Subscription type split: processed {Count} records.", batch.Count);
            totalProcessed += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        logger.LogInformation("Subscription type split migration total processed {Total} records.", totalProcessed);

        // 使用 upsert 避免重复键错误（回滚场景 / 双服务并发）
        await MarkMigrationAppliedAsync(SubscriptionTypeSplitMigrationKey, ct);
        logger.LogInformation("Subscription type split migration completed and marked as applied.");
    }

    public const string LiveEventPayloadObjectBackfillMigrationKey = "live_event_payload_object_backfill_v1";
    private static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromMinutes(30);

    public async Task BackfillLiveEventPayloadObjectAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);

        // Atomic lock: only one instance wins the race
        var filter = Builders<MetaRecord>.Filter.Eq(r => r.Key, LiveEventPayloadObjectBackfillMigrationKey);
        var now = DateTime.UtcNow;

        // Case 1: marker already applied → skip
        var existing = await meta.Find(filter).FirstOrDefaultAsync(ct);
        if (existing?.Status == "applied")
        {
            logger.LogInformation("Live event payload object backfill already applied; skipping.");
            return;
        }

        // Case 2: marker in "running" and not stale → skip (another instance is doing it)
        if (existing?.Status == "running" && existing.StartedAt.HasValue && (now - existing.StartedAt.Value) < StaleRunningThreshold)
        {
            logger.LogInformation("Live event payload object backfill is running in another instance; skipping.");
            return;
        }

        // Case 3: marker in "running" but stale (process crashed) → take over
        if (existing?.Status == "running" && existing.StartedAt.HasValue && (now - existing.StartedAt.Value) >= StaleRunningThreshold)
        {
            logger.LogWarning("Live event payload object backfill stale running detected (started at {StartedAt}); taking over.", existing.StartedAt.Value);
        }

        // Case 4: no marker, "applied" without Status (legacy), or stale running → try atomic acquire
        var update = Builders<MetaRecord>.Update
            .SetOnInsert(r => r.Status, "running")
            .SetOnInsert(r => r.StartedAt, now);
        var options = new FindOneAndUpdateOptions<MetaRecord> { IsUpsert = true, ReturnDocument = ReturnDocument.Before };

        var acquired = await meta.FindOneAndUpdateAsync(filter, update, options, ct);

        // If acquired is null → new document was inserted by us → we won the lock
        // If acquired is not null and Status is "applied" → someone finished between our read and now → skip
        // If acquired is not null and Status is "running" with our StartedAt → we are the owner (first caller)
        // If acquired is not null and Status is "running" with different StartedAt → skip (another instance won)
        if (acquired is not null)
        {
            if (acquired.Status == "applied")
            {
                logger.LogInformation("Live event payload object backfill already applied; skipping.");
                return;
            }

            if (acquired.Status == "running" && acquired.StartedAt.HasValue && acquired.StartedAt.Value != now)
            {
                // Another instance holds the lock and it's not stale (checked above)
                if ((now - acquired.StartedAt.Value) < StaleRunningThreshold)
                {
                    logger.LogInformation("Live event payload object backfill is running in another instance; skipping.");
                    return;
                }
                // Stale but we already tried to acquire above — check if we won via SetOnInsert
                // If the ReplaceOne below changes nothing, it means another instance inserted first
                var replaced = await meta.ReplaceOneAsync(
                    Builders<MetaRecord>.Filter.And(filter,
                        Builders<MetaRecord>.Filter.Eq(r => r.Status, "running"),
                        Builders<MetaRecord>.Filter.Lt(r => r.StartedAt, now - StaleRunningThreshold)),
                    new MetaRecord { Key = LiveEventPayloadObjectBackfillMigrationKey, Status = "running", StartedAt = now },
                    new ReplaceOptions { IsUpsert = false },
                    ct);

                if (replaced.MatchedCount == 0)
                {
                    logger.LogInformation("Live event payload object backfill is running in another instance; skipping.");
                    return;
                }
            }
        }

        logger.LogInformation("Live event payload object backfill acquired lock; starting migration.");

        const int batchSize = 500;
        var rawCollection = database.GetCollection<BsonDocument>(MongoCollectionNames.LiveEvent);

        // 过滤 Payload 字段为 BSON String 类型的文档（绕过实体序列化器）
        var typeFilter = Builders<BsonDocument>.Filter.Type("Payload", BsonType.String);

        MongoDB.Bson.ObjectId lastId = MongoDB.Bson.ObjectId.Empty;
        long totalProcessed = 0;
        long skippedCount = 0;

        while (true)
        {
            var idFilter = Builders<BsonDocument>.Filter.Gt("_id", lastId);
            var combinedFilter = Builders<BsonDocument>.Filter.And(typeFilter, idFilter);

            var batch = await rawCollection.Find(combinedFilter)
                .Project<BsonDocument>(Builders<BsonDocument>.Projection
                    .Include("_id")
                    .Include("Type")
                    .Include("Payload"))
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .Limit(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var models = new List<WriteModel<BsonDocument>>(batch.Count);
            foreach (var doc in batch)
            {
                var id = doc["_id"].AsObjectId;
                var type = doc["Type"].AsString;
                var payloadBsonValue = doc["Payload"];

                if (payloadBsonValue.BsonType != BsonType.String)
                {
                    skippedCount++;
                    continue;
                }

                var rawPayloadString = payloadBsonValue.AsString;

                if (LiveEventPayloadParser.TryToBsonDocument(rawPayloadString, out var bsonDoc))
                {
                    models.Add(new UpdateOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", id),
                        Builders<BsonDocument>.Update.Set("Payload", bsonDoc)));
                }
                else
                {
                    logger.LogWarning("Skipping dirty record {Id} (Type={Type}): payload is not a valid JSON object, preserving original string.", id, type);
                    skippedCount++;
                }
            }

            if (models.Count > 0)
            {
                await rawCollection.BulkWriteAsync(models, cancellationToken: ct);
                logger.LogInformation("Backfilled payload object for {Count} records.", models.Count);
            }

            lastId = batch[^1]["_id"].AsObjectId;
            totalProcessed += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        logger.LogInformation("Live event payload object backfill total processed {Total} records, skipped {Skipped} (dirty or non-JSON-object).", totalProcessed, skippedCount);

        // Mark as applied
        await meta.ReplaceOneAsync(
            Builders<MetaRecord>.Filter.And(
                Builders<MetaRecord>.Filter.Eq(r => r.Key, LiveEventPayloadObjectBackfillMigrationKey),
                Builders<MetaRecord>.Filter.Eq(r => r.Status, "running")),
            new MetaRecord { Key = LiveEventPayloadObjectBackfillMigrationKey, Status = "applied", StartedAt = now },
            new ReplaceOptions { IsUpsert = true },
            ct);

        logger.LogInformation("Live event payload object backfill completed and marked as applied.");
    }

    private async Task EnsureNotificationSubscriptionIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<NotificationSubscriptionRecord>(MongoCollectionNames.NotificationSubscriptions);
        var keys = Builders<NotificationSubscriptionRecord>.IndexKeys
            .Ascending(n => n.EventType)
            .Ascending(n => n.GroupId);
        var options = new CreateIndexOptions { Unique = true, Name = "EventType_1_GroupId_1" };
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<NotificationSubscriptionRecord>(keys, options), cancellationToken: ct);
        logger.LogInformation("Ensured (EventType, GroupId) composite unique index on notification_subscriptions.");
    }

    private async Task EnsureWeiboCookieIndexesAsync(CancellationToken ct)
    {
        // weibo_cookies indexes (same structure as bilibili_cookies)
        var cookiesCollection = database.GetCollection<CookieRecord>(MongoCollectionNames.WeiboCookies);
        var cookieKeys = Builders<CookieRecord>.IndexKeys
            .Ascending(c => c.Status)
            .Ascending(c => c.LastUsedAt);
        await cookiesCollection.Indexes.CreateOneAsync(new CreateIndexModel<CookieRecord>(cookieKeys), cancellationToken: ct);

        var cookieAccountIdKeys = Builders<CookieRecord>.IndexKeys.Ascending(c => c.AccountId);
        await cookiesCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieRecord>(cookieAccountIdKeys, new CreateIndexOptions { Name = "AccountId_1" }),
            cancellationToken: ct);

        // weibo_cookie_sessions indexes (same structure as bilibili_cookie_sessions)
        var sessionsCollection = database.GetCollection<CookieSessionRecord>(MongoCollectionNames.WeiboCookieSessions);
        var sessionStatusKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.Status);
        await sessionsCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(sessionStatusKeys, new CreateIndexOptions { Name = "Status_1" }),
            cancellationToken: ct);

        var sessionCookieIdKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.CookieId);
        await sessionsCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(sessionCookieIdKeys, new CreateIndexOptions { Name = "CookieId_1" }),
            cancellationToken: ct);

        logger.LogInformation("Ensured weibo cookie indexes.");
    }

    public async Task MigrateCookieInvalidEventTypeSplitAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == CookieInvalidEventTypeSplitMigrationKey)
            .AnyAsync(ct);

        // 即使 marker 存在，也需要检查是否有未迁移的记录（回滚场景）
        var collection = database.GetCollection<NotificationSubscriptionRecord>(MongoCollectionNames.NotificationSubscriptions);
        var cookieInvalidFilter = Builders<NotificationSubscriptionRecord>.Filter.Eq(n => n.EventType, "cookie_invalid");
        var hasUnmigrated = await collection.Find(cookieInvalidFilter).AnyAsync(ct);

        if (markerExists && !hasUnmigrated)
        {
            logger.LogInformation("Cookie invalid event type split migration already applied; skipping.");
            return;
        }

        // 批量更新 cookie_invalid → bilibili_cookie_invalid
        const int batchSize = 500;
        long totalProcessed = 0;

        while (true)
        {
            var batch = await collection.Find(cookieInvalidFilter)
                .Limit(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var ids = batch.Select(r => r.Id).ToList();
            var updateFilter = Builders<NotificationSubscriptionRecord>.Filter.In(n => n.Id, ids);
            var update = Builders<NotificationSubscriptionRecord>.Update.Set(n => n.EventType, "bilibili_cookie_invalid");
            await collection.UpdateManyAsync(updateFilter, update, cancellationToken: ct);

            logger.LogInformation("Cookie invalid event type split: processed {Count} records.", batch.Count);
            totalProcessed += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        logger.LogInformation("Cookie invalid event type split migration total processed {Total} records.", totalProcessed);

        // 使用 upsert 避免重复键错误（回滚场景 / 双服务并发）
        await MarkMigrationAppliedAsync(CookieInvalidEventTypeSplitMigrationKey, ct);
        logger.LogInformation("Cookie invalid event type split migration completed and marked as applied.");
    }
}

public sealed class MetaRecord
{
    [MongoDB.Bson.Serialization.Attributes.BsonId]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Migration status: "applied" (completed) or "running" (in progress by another instance).
    /// Only used by migrations that need distributed locking (e.g. BackfillLiveEventPayloadObjectAsync).
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Timestamp when the migration was started (used to detect stale "running" state).
    /// </summary>
    public DateTime? StartedAt { get; set; }
}
