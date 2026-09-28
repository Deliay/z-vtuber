using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class WeiboMongoSetup(IMongoDatabase database, ILogger<WeiboMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await EnsureWeiboSubscriptionIndexesAsync(ct);
        await EnsureWeiboTimelineIndexesAsync(ct);
        Logger.LogInformation("MongoDB weibo indexes ensured for database {Database}", DatabaseName);
    }

    private Task EnsureWeiboSubscriptionIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<WeiboSubscriptionRecord>(WeiboCollectionNames.WeiboSubscriptions);
        var keys = Builders<WeiboSubscriptionRecord>.IndexKeys.Ascending(s => s.Uid);
        var options = new CreateIndexOptions { Unique = true, Name = "Uid_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<WeiboSubscriptionRecord>(keys, options), cancellationToken: ct);
    }

    private async Task EnsureWeiboTimelineIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<WeiboTimelineRecord>(WeiboCollectionNames.WeiboUserTimeline);

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
}
