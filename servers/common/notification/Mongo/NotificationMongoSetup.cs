using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class NotificationMongoSetup(IMongoDatabase database, ILogger<NotificationMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var collection = Database.GetCollection<NotificationSubscriptionRecord>(NotificationCollectionNames.NotificationSubscriptions);
        var keys = Builders<NotificationSubscriptionRecord>.IndexKeys
            .Ascending(n => n.EventType)
            .Ascending(n => n.GroupId);
        var options = new CreateIndexOptions { Unique = true, Name = "EventType_1_GroupId_1" };
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<NotificationSubscriptionRecord>(keys, options), cancellationToken: ct);
        Logger.LogInformation("Ensured (EventType, GroupId) composite unique index on notification_subscriptions.");
    }

    public const string CookieInvalidEventTypeSplitMigrationKey = "cookie_invalid_platform_split_v1";

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await MigrateCookieInvalidEventTypeSplitAsync(ct);
    }

    public async Task MigrateCookieInvalidEventTypeSplitAsync(CancellationToken ct = default)
    {
        var meta = MetaCollection;
        var markerExists = await meta.Find(r => r.Key == CookieInvalidEventTypeSplitMigrationKey)
            .AnyAsync(ct);

        // 即使 marker 存在，也需要检查是否有未迁移的记录（回滚场景）
        var collection = Database.GetCollection<NotificationSubscriptionRecord>(NotificationCollectionNames.NotificationSubscriptions);
        var cookieInvalidFilter = Builders<NotificationSubscriptionRecord>.Filter.Eq(n => n.EventType, "cookie_invalid");
        var hasUnmigrated = await collection.Find(cookieInvalidFilter).AnyAsync(ct);

        if (markerExists && !hasUnmigrated)
        {
            Logger.LogInformation("Cookie invalid event type split migration already applied; skipping.");
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

            Logger.LogInformation("Cookie invalid event type split: processed {Count} records.", batch.Count);
            totalProcessed += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        Logger.LogInformation("Cookie invalid event type split migration total processed {Total} records.", totalProcessed);

        // 使用 upsert 避免重复键错误（回滚场景 / 双服务并发）
        await MarkMigrationAppliedAsync(CookieInvalidEventTypeSplitMigrationKey, ct);
        Logger.LogInformation("Cookie invalid event type split migration completed and marked as applied.");
    }
}
