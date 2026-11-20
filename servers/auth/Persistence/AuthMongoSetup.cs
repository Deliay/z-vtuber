using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Auth.Persistence;

/// <summary>auth 集合与索引初始化（幂等，启动时执行一次）。</summary>
public sealed class AuthMongoSetup(IMongoDatabase database, ILogger<AuthMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await EnsureLoginRequestIndexesAsync(ct);
        await EnsureUserIndexesAsync(ct);
        await EnsureProviderSessionIndexesAsync(ct);
        await EnsureTicketIndexesAsync(ct);
        await EnsureOAuthCodeIndexesAsync(ct);
        await EnsureProviderTokenIndexesAsync(ct);
        await EnsureSessionIndexesAsync(ct);
        await EnsureAuditIndexesAsync(ct);
        Logger.LogInformation("MongoDB auth indexes ensured for database {Database}", DatabaseName);
    }

    private async Task EnsureLoginRequestIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthLoginRequestRecord>(AuthCollectionNames.LoginRequests);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthLoginRequestRecord>(
            Builders<AuthLoginRequestRecord>.IndexKeys.Ascending(x => x.RequestId),
            new CreateIndexOptions { Unique = true, Name = "RequestId_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthLoginRequestRecord>(
            Builders<AuthLoginRequestRecord>.IndexKeys.Ascending(x => x.TokenHash),
            new CreateIndexOptions { Unique = true, Name = "TokenHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthLoginRequestRecord>(
            Builders<AuthLoginRequestRecord>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.ExpiresAt),
            new CreateIndexOptions { Name = "Status_1_ExpiresAt_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthLoginRequestRecord>(
            Builders<AuthLoginRequestRecord>.IndexKeys.Ascending(x => x.SenderUid).Descending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "SenderUid_1_CreatedAt_-1" }), cancellationToken: ct);

        // 保留 1 小时用于区分 404 / expired；真实状态由代码用 ExpiresAt 判定。
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthLoginRequestRecord>(
            Builders<AuthLoginRequestRecord>.IndexKeys.Ascending(x => x.ExpiresAt),
            new CreateIndexOptions { Name = "ExpiresAt_1_TTL", ExpireAfter = TimeSpan.FromHours(1) }), cancellationToken: ct);
    }

    private async Task EnsureUserIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthUserRecord>(AuthCollectionNames.Users);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthUserRecord>(
            Builders<AuthUserRecord>.IndexKeys.Ascending(x => x.Platform).Ascending(x => x.PlatformUid),
            new CreateIndexOptions { Unique = true, Name = "Platform_1_PlatformUid_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthUserRecord>(
            Builders<AuthUserRecord>.IndexKeys.Ascending(x => x.ProviderSubject),
            new CreateIndexOptions { Unique = true, Name = "ProviderSubject_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthUserRecord>(
            Builders<AuthUserRecord>.IndexKeys.Ascending(x => x.Status).Descending(x => x.LastLoginAt),
            new CreateIndexOptions { Name = "Status_1_LastLoginAt_-1" }), cancellationToken: ct);
    }

    private async Task EnsureProviderSessionIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthProviderSessionRecord>(AuthCollectionNames.ProviderSessions);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderSessionRecord>(
            Builders<AuthProviderSessionRecord>.IndexKeys.Ascending(x => x.SessionIdHash),
            new CreateIndexOptions { Unique = true, Name = "SessionIdHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderSessionRecord>(
            Builders<AuthProviderSessionRecord>.IndexKeys.Ascending(x => x.UserId).Descending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "UserId_1_CreatedAt_-1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderSessionRecord>(
            Builders<AuthProviderSessionRecord>.IndexKeys.Ascending(x => x.ExpiresAt),
            new CreateIndexOptions { Name = "ExpiresAt_1_TTL", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }

    private async Task EnsureTicketIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthProviderTicketRecord>(AuthCollectionNames.ProviderTickets);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderTicketRecord>(
            Builders<AuthProviderTicketRecord>.IndexKeys.Ascending(x => x.TicketHash),
            new CreateIndexOptions { Unique = true, Name = "TicketHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderTicketRecord>(
            Builders<AuthProviderTicketRecord>.IndexKeys.Ascending(x => x.ExpiresAt),
            new CreateIndexOptions { Name = "ExpiresAt_1_TTL", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }

    private async Task EnsureOAuthCodeIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthOAuthCodeRecord>(AuthCollectionNames.OAuthCodes);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthOAuthCodeRecord>(
            Builders<AuthOAuthCodeRecord>.IndexKeys.Ascending(x => x.CodeHash),
            new CreateIndexOptions { Unique = true, Name = "CodeHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthOAuthCodeRecord>(
            Builders<AuthOAuthCodeRecord>.IndexKeys.Ascending(x => x.ExpiresAt),
            new CreateIndexOptions { Name = "ExpiresAt_1_TTL", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthOAuthCodeRecord>(
            Builders<AuthOAuthCodeRecord>.IndexKeys.Ascending(x => x.ConsumedAt),
            new CreateIndexOptions { Name = "ConsumedAt_1" }), cancellationToken: ct);
    }

    private async Task EnsureProviderTokenIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthProviderTokenRecord>(AuthCollectionNames.ProviderTokens);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderTokenRecord>(
            Builders<AuthProviderTokenRecord>.IndexKeys.Ascending(x => x.AccessTokenHash),
            new CreateIndexOptions { Unique = true, Name = "AccessTokenHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderTokenRecord>(
            Builders<AuthProviderTokenRecord>.IndexKeys.Ascending(x => x.RefreshTokenHash),
            new CreateIndexOptions { Unique = true, Sparse = true, Name = "RefreshTokenHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderTokenRecord>(
            Builders<AuthProviderTokenRecord>.IndexKeys.Ascending(x => x.UserId).Descending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "UserId_1_CreatedAt_-1" }), cancellationToken: ct);

        // 只在 refresh 到期上建 TTL：access 过期由代码判断，避免整条文档被删除导致 refresh 失效。
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthProviderTokenRecord>(
            Builders<AuthProviderTokenRecord>.IndexKeys.Ascending(x => x.RefreshExpiresAt),
            new CreateIndexOptions { Name = "RefreshExpiresAt_1_TTL", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }

    private async Task EnsureSessionIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthSessionRecord>(AuthCollectionNames.Sessions);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthSessionRecord>(
            Builders<AuthSessionRecord>.IndexKeys.Ascending(x => x.AccessTokenHash),
            new CreateIndexOptions { Unique = true, Name = "AccessTokenHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthSessionRecord>(
            Builders<AuthSessionRecord>.IndexKeys.Ascending(x => x.RefreshTokenHash),
            new CreateIndexOptions { Unique = true, Name = "RefreshTokenHash_1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthSessionRecord>(
            Builders<AuthSessionRecord>.IndexKeys.Ascending(x => x.UserId).Descending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "UserId_1_CreatedAt_-1" }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthSessionRecord>(
            Builders<AuthSessionRecord>.IndexKeys.Ascending(x => x.RefreshExpiresAt),
            new CreateIndexOptions { Name = "RefreshExpiresAt_1_TTL", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }

    private async Task EnsureAuditIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<AuthAuditLogRecord>(AuthCollectionNames.AuditLogs);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthAuditLogRecord>(
            Builders<AuthAuditLogRecord>.IndexKeys.Descending(x => x.OccurredAt),
            new CreateIndexOptions { Name = "OccurredAt_-1_TTL", ExpireAfter = TimeSpan.FromDays(90) }), cancellationToken: ct);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<AuthAuditLogRecord>(
            Builders<AuthAuditLogRecord>.IndexKeys.Ascending(x => x.RequestId).Descending(x => x.OccurredAt),
            new CreateIndexOptions { Name = "RequestId_1_OccurredAt_-1" }), cancellationToken: ct);
    }
}
