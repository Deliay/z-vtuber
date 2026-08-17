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
        var keys = Builders<SubscriptionRecord>.IndexKeys.Ascending(s => s.Mid);
        var options = new CreateIndexOptions { Unique = true };
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

    private Task EnsureLiveEventIndexesAsync(CancellationToken ct)
    {
        var collection = database.GetCollection<LiveEventRecord>(MongoCollectionNames.LiveEvent);
        var keys = Builders<LiveEventRecord>.IndexKeys
            .Ascending(e => e.RoomId)
            .Ascending(e => e.OccurredAt);
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<LiveEventRecord>(keys), cancellationToken: ct);
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

        await meta.InsertOneAsync(new MetaRecord { Key = SpaceTimelinePubTsMigrationKey }, ct);
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

        var filter = Builders<LiveEventRecord>.Filter.And(
            Builders<LiveEventRecord>.Filter.Eq(e => e.Type, "gift"),
            Builders<LiveEventRecord>.Filter.Or(
                Builders<LiveEventRecord>.Filter.Not(Builders<LiveEventRecord>.Filter.Exists(e => e.Price)),
                Builders<LiveEventRecord>.Filter.Eq(e => e.Price, 0L)));

        long processed = batchSize;
        do
        {
            var cursor = await collection.Find(filter)
                .Project<LiveEventRecord>(Builders<LiveEventRecord>.Projection
                    .Include(r => r.Id)
                    .Include(r => r.Type)
                    .Include(r => r.Payload))
                .Limit(batchSize)
                .ToCursorAsync(ct);

            var batch = await cursor.ToListAsync(ct);
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
            processed = batch.Count;
        }
        while (processed == batchSize);

        await meta.InsertOneAsync(new MetaRecord { Key = GiftPriceBackfillMigrationKey }, ct);
        logger.LogInformation("Gift price backfill completed and marked as applied.");
    }
}

public sealed class MetaRecord
{
    [MongoDB.Bson.Serialization.Attributes.BsonId]
    public string Key { get; set; } = string.Empty;
}
