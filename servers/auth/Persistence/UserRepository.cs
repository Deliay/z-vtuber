using MongoDB.Driver;

namespace Z.Vtuber.Auth.Persistence;

public sealed class UserRepository
{
    private readonly IMongoCollection<AuthUserRecord> _collection;

    public UserRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthUserRecord>(AuthCollectionNames.Users);
    }

    public Task<AuthUserRecord?> FindByPlatformUidAsync(
        string platform, long uid, CancellationToken ct = default) =>
        _collection.Find(x => x.Platform == platform && x.PlatformUid == uid).FirstOrDefaultAsync(ct)!;

    public Task<AuthUserRecord?> FindByIdAsync(MongoDB.Bson.ObjectId id, CancellationToken ct = default) =>
        _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct)!;

    public Task InsertAsync(AuthUserRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthUserRecord?> UpdateLoginAsync(
        MongoDB.Bson.ObjectId id,
        string displayName,
        string? avatarUrl,
        string? lastSourceRoomId,
        string? lastLoginIp,
        DateTime now,
        CancellationToken ct = default)
    {
        var update = Builders<AuthUserRecord>.Update
            .Set(x => x.LastSourceRoomId, lastSourceRoomId)
            .Set(x => x.LastLoginIp, lastLoginIp)
            .Set(x => x.LastLoginAt, now)
            .Inc(x => x.LoginCount, 1);

        // 资料降级：仅在本次解析到非空值时覆盖，避免头像/昵称解析失败清掉已有资料。
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            update = update.Set(x => x.DisplayName, displayName);
        }

        if (!string.IsNullOrWhiteSpace(avatarUrl))
        {
            update = update.Set(x => x.AvatarUrl, avatarUrl);
        }

        return _collection.FindOneAndUpdateAsync(
            x => x.Id == id, update,
            new FindOneAndUpdateOptions<AuthUserRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }

    public Task<List<AuthUserRecord>> FindAllAsync(CancellationToken ct = default) =>
        _collection.Find(FilterDefinition<AuthUserRecord>.Empty).ToListAsync(ct);
}
