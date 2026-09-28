using MongoDB.Driver;
using MongoDB.Bson;

namespace Z.Vtuber.Common.Persistence;

/// <summary>
/// Mongo 初始化基座：提供数据库访问、迁移 marker 读写与索引删除助手。
/// 各领域索引初始化与迁移见对应领域的 XxxMongoSetup。
/// </summary>
public class MongoDbInitializer
{
    public const string DatabaseName = "zvtuber";

    protected IMongoDatabase Database { get; }

    protected ILogger Logger { get; }

    public MongoDbInitializer(IMongoDatabase database, ILogger<MongoDbInitializer> logger)
        : this(database, (ILogger)logger)
    {
    }

    protected MongoDbInitializer(IMongoDatabase database, ILogger logger)
    {
        Database = database;
        Logger = logger;
    }

    protected IMongoCollection<MetaRecord> MetaCollection =>
        Database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);

    /// <summary>
    /// 判断迁移 marker 是否已写入（zvtuber_meta 集合，Key 为迁移 key）。
    /// </summary>
    public async Task<bool> IsMigrationAppliedAsync(string key, CancellationToken ct = default)
    {
        var meta = MetaCollection;
        return await meta.Find(r => r.Key == key).AnyAsync(ct);
    }

    /// <summary>
    /// 使用 upsert 写入迁移 marker，避免多服务并发启动时重复键错误。
    /// </summary>
    public async Task MarkMigrationAppliedAsync(string key, CancellationToken ct = default)
    {
        var meta = MetaCollection;
        var filter = Builders<MetaRecord>.Filter.Eq(r => r.Key, key);
        await meta.ReplaceOneAsync(filter, new MetaRecord { Key = key }, new ReplaceOptions { IsUpsert = true }, ct);
    }

    protected static async Task DropIndexIfExistsAsync<T>(IMongoCollection<T> collection, string name, CancellationToken ct)
    {
        using var cursor = await collection.Indexes.ListAsync(ct);
        var indexes = await cursor.ToListAsync(ct);
        if (indexes.Any(i => i["name"].AsString == name))
        {
            await collection.Indexes.DropOneAsync(name, ct);
        }
    }
}
