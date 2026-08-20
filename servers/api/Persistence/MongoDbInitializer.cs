using MongoDB.Driver;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Persistence;

public sealed class MongoCollectionNames
{
    public const string Cookies = "bilibili_cookies";
    public const string Subscriptions = "bilibili_subscriptions";
    public const string AccountBasicInfo = "bilibili_account_basic_info";
    public const string LiveStatus = "bilibili_live_status";
    public const string LiveEvent = "bilibili_live_event";
    public const string SpaceTimeline = "bilibili_user_space_timeline";
    public const string LiveSession = "bilibili_live_session";
    public const string Meta = "zvtuber_meta";
    public const string LiveEventTextIndex = "bilibili_live_event_text_index";
}

public sealed class MongoDbInitializer(IMongoDatabase database, ILogger<MongoDbInitializer> logger)
{
    public const string DatabaseName = "zvtuber";

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await EnsureCookiesIndexesAsync(ct);
        await EnsureSubscriptionsIndexesAsync(ct);
        await EnsureAccountBasicInfoIndexesAsync(ct);
        await EnsureLiveStatusIndexesAsync(ct);
        await EnsureLiveEventIndexesAsync(ct);
        await EnsureSpaceTimelineIndexesAsync(ct);
        await EnsureLiveSessionIndexesAsync(ct);
        await EnsureLiveEventTextIndexAsync(ct);
        logger.LogInformation("MongoDB indexes ensured for database {Database}", DatabaseName);
    }

    private Task EnsureCookiesIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<CookieRecord>(MongoCollectionNames.Cookies);
        var keys = Builders<CookieRecord>.IndexKeys
            .Ascending(c => c.Status)
            .Ascending(c => c.LastUsedAt);
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<CookieRecord>(keys), cancellationToken: ct);
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

        await meta.InsertOneAsync(new MetaRecord { Key = SpaceTimelinePubTsMigrationKey }, options: null, ct);
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
                var price = Live.GiftPriceExtractor.ExtractGoldSeeds(record.Type, record.Payload);
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

        await meta.InsertOneAsync(new MetaRecord { Key = GiftPriceBackfillMigrationKey }, options: null, ct);
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

            var models = new List<WriteModel<LiveEventTextIndexRecord>>(batch.Count);
            foreach (var record in batch)
            {
                var text = Live.LiveEventTextExtractor.ExtractText(record.Type, record.Payload);
                if (text is null)
                {
                    continue;
                }

                var textIndexRecord = new LiveEventTextIndexRecord
                {
                    Id = MongoDB.Bson.ObjectId.GenerateNewId(),
                    EventId = record.Id,
                    RoomId = record.RoomId,
                    Type = record.Type,
                    Text = text,
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

        await meta.InsertOneAsync(new MetaRecord { Key = LiveEventTextIndexBackfillMigrationKey }, options: null, ct);
        logger.LogInformation("Live event text index backfill completed and marked as applied.");
    }

    public const string SubscriptionTypeSplitMigrationKey = "subscription_type_split_v1";

    public async Task MigrateSubscriptionTypeSplitAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == SubscriptionTypeSplitMigrationKey)
            .AnyAsync(ct);
        if (markerExists)
        {
            logger.LogInformation("Subscription type split migration already applied; skipping.");
            return;
        }

        const int batchSize = 500;
        var collection = database.GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);

        // 查找所有无 Type 字段或 Type 为空的存量文档
        var noTypeFilter = Builders<SubscriptionRecord>.Filter.Or(
            Builders<SubscriptionRecord>.Filter.Not(Builders<SubscriptionRecord>.Filter.Exists(s => s.Type)),
            Builders<SubscriptionRecord>.Filter.Eq(s => s.Type, default(SubscriptionType)));

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

            var models = new List<WriteModel<SubscriptionRecord>>(batch.Count);
            foreach (var record in batch)
            {
                // 1. 更新原记录为 dynamic 类型
                models.Add(new UpdateOneModel<SubscriptionRecord>(
                    Builders<SubscriptionRecord>.Filter.Eq(s => s.Id, record.Id),
                    Builders<SubscriptionRecord>.Update.Set(s => s.Type, SubscriptionType.Dynamic)));

                // 2. 插入 danmaku 类型的第二条记录
                models.Add(new InsertOneModel<SubscriptionRecord>(new SubscriptionRecord
                {
                    Mid = record.Mid,
                    Type = SubscriptionType.Danmaku,
                    SubscribedAt = record.SubscribedAt
                }));
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

        await meta.InsertOneAsync(new MetaRecord { Key = SubscriptionTypeSplitMigrationKey }, options: null, ct);
        logger.LogInformation("Subscription type split migration completed and marked as applied.");
    }
}

public sealed class MetaRecord
{
    [MongoDB.Bson.Serialization.Attributes.BsonId]
    public string Key { get; set; } = string.Empty;
}
