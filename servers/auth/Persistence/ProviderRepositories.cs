using MongoDB.Bson;
using MongoDB.Driver;

namespace Z.Vtuber.Auth.Persistence;

public sealed class ProviderSessionRepository
{
    private readonly IMongoCollection<AuthProviderSessionRecord> _collection;

    public ProviderSessionRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthProviderSessionRecord>(AuthCollectionNames.ProviderSessions);
    }

    public Task InsertAsync(AuthProviderSessionRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthProviderSessionRecord?> FindByHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.SessionIdHash == hash).FirstOrDefaultAsync(ct)!;

    public async Task TouchAsync(ObjectId id, DateTime now, DateTime expiresAt, CancellationToken ct = default)
    {
        var update = Builders<AuthProviderSessionRecord>.Update
            .Set(x => x.LastSeenAt, now)
            .Set(x => x.ExpiresAt, expiresAt);
        await _collection.UpdateOneAsync(x => x.Id == id, update, cancellationToken: ct);
    }

    public Task<long> RevokeByHashAsync(string hash, DateTime now, CancellationToken ct = default) =>
        _collection.UpdateOneAsync(
            x => x.SessionIdHash == hash && x.RevokedAt == null,
            Builders<AuthProviderSessionRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct).ContinueWith(t => t.Result.ModifiedCount, ct);

    public async Task<long> RevokeAllByUserAsync(ObjectId userId, DateTime now, CancellationToken ct = default)
    {
        var result = await _collection.UpdateManyAsync(
            x => x.UserId == userId && x.RevokedAt == null,
            Builders<AuthProviderSessionRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct);
        return result.ModifiedCount;
    }
}

public sealed class ProviderTicketRepository
{
    private readonly IMongoCollection<AuthProviderTicketRecord> _collection;

    public ProviderTicketRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthProviderTicketRecord>(AuthCollectionNames.ProviderTickets);
    }

    public Task InsertAsync(AuthProviderTicketRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthProviderTicketRecord?> FindByHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.TicketHash == hash).FirstOrDefaultAsync(ct)!;

    /// <summary>一次性消费：<c>ConsumedAt == null &amp;&amp; ExpiresAt &gt; now</c>。</summary>
    public Task<AuthProviderTicketRecord?> TryConsumeAsync(
        string hash, string clientId, string redirectUri, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthProviderTicketRecord>.Filter.And(
            Builders<AuthProviderTicketRecord>.Filter.Eq(x => x.TicketHash, hash),
            Builders<AuthProviderTicketRecord>.Filter.Eq(x => x.ClientId, clientId),
            Builders<AuthProviderTicketRecord>.Filter.Eq(x => x.RedirectUri, redirectUri),
            Builders<AuthProviderTicketRecord>.Filter.Eq(x => x.ConsumedAt, null),
            Builders<AuthProviderTicketRecord>.Filter.Gt(x => x.ExpiresAt, now));

        var update = Builders<AuthProviderTicketRecord>.Update.Set(x => x.ConsumedAt, now);
        return _collection.FindOneAndUpdateAsync(
            filter, update,
            new FindOneAndUpdateOptions<AuthProviderTicketRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }

    public Task RevokeAsync(string ticketHash, DateTime now, CancellationToken ct = default) =>
        _collection.UpdateOneAsync(
            x => x.TicketHash == ticketHash && x.ConsumedAt == null,
            Builders<AuthProviderTicketRecord>.Update.Set(x => x.ConsumedAt, now),
            cancellationToken: ct);
}

public sealed class OAuthCodeRepository
{
    private readonly IMongoCollection<AuthOAuthCodeRecord> _collection;

    public OAuthCodeRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthOAuthCodeRecord>(AuthCollectionNames.OAuthCodes);
    }

    public Task InsertAsync(AuthOAuthCodeRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthOAuthCodeRecord?> FindByHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.CodeHash == hash).FirstOrDefaultAsync(ct)!;

    public Task<AuthOAuthCodeRecord?> TryConsumeAsync(string hash, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthOAuthCodeRecord>.Filter.And(
            Builders<AuthOAuthCodeRecord>.Filter.Eq(x => x.CodeHash, hash),
            Builders<AuthOAuthCodeRecord>.Filter.Eq(x => x.ConsumedAt, null),
            Builders<AuthOAuthCodeRecord>.Filter.Gt(x => x.ExpiresAt, now));

        var update = Builders<AuthOAuthCodeRecord>.Update.Set(x => x.ConsumedAt, now);
        return _collection.FindOneAndUpdateAsync(
            filter, update,
            new FindOneAndUpdateOptions<AuthOAuthCodeRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }
}

public sealed class ProviderTokenRepository
{
    private readonly IMongoCollection<AuthProviderTokenRecord> _collection;

    public ProviderTokenRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthProviderTokenRecord>(AuthCollectionNames.ProviderTokens);
    }

    public Task InsertAsync(AuthProviderTokenRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthProviderTokenRecord?> FindByAccessHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.AccessTokenHash == hash).FirstOrDefaultAsync(ct)!;

    public Task<AuthProviderTokenRecord?> FindByRefreshHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.RefreshTokenHash == hash).FirstOrDefaultAsync(ct)!;

    public Task<AuthProviderTokenRecord?> RotateRefreshAsync(
        string oldRefreshHash,
        string newAccessHash,
        string newRefreshHash,
        DateTime accessExpiresAt,
        DateTime refreshExpiresAt,
        DateTime now,
        CancellationToken ct = default)
    {
        var filter = Builders<AuthProviderTokenRecord>.Filter.And(
            Builders<AuthProviderTokenRecord>.Filter.Eq(x => x.RefreshTokenHash, oldRefreshHash),
            Builders<AuthProviderTokenRecord>.Filter.Eq(x => x.RevokedAt, null),
            Builders<AuthProviderTokenRecord>.Filter.Gt(x => x.RefreshExpiresAt, now));

        var update = Builders<AuthProviderTokenRecord>.Update
            .Set(x => x.AccessTokenHash, newAccessHash)
            .Set(x => x.RefreshTokenHash, newRefreshHash)
            .Set(x => x.ExpiresAt, accessExpiresAt)
            .Set(x => x.RefreshExpiresAt, refreshExpiresAt);

        return _collection.FindOneAndUpdateAsync(
            filter, update,
            new FindOneAndUpdateOptions<AuthProviderTokenRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }

    public async Task RevokeByAccessHashAsync(string hash, DateTime now, CancellationToken ct = default)
    {
        await _collection.UpdateOneAsync(
            x => x.AccessTokenHash == hash && x.RevokedAt == null,
            Builders<AuthProviderTokenRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct);
    }

    public async Task RevokeByRefreshHashAsync(string hash, DateTime now, CancellationToken ct = default)
    {
        await _collection.UpdateOneAsync(
            x => x.RefreshTokenHash == hash && x.RevokedAt == null,
            Builders<AuthProviderTokenRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct);
    }

    public async Task<long> RevokeAllByUserAsync(ObjectId userId, DateTime now, CancellationToken ct = default)
    {
        var result = await _collection.UpdateManyAsync(
            x => x.UserId == userId && x.RevokedAt == null,
            Builders<AuthProviderTokenRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct);
        return result.ModifiedCount;
    }
}

public sealed class StandaloneSessionRepository
{
    private readonly IMongoCollection<AuthSessionRecord> _collection;

    public StandaloneSessionRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthSessionRecord>(AuthCollectionNames.Sessions);
    }

    public Task InsertAsync(AuthSessionRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthSessionRecord?> FindByAccessHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.AccessTokenHash == hash).FirstOrDefaultAsync(ct)!;

    public Task<AuthSessionRecord?> FindByRefreshHashAsync(string hash, CancellationToken ct = default) =>
        _collection.Find(x => x.RefreshTokenHash == hash).FirstOrDefaultAsync(ct)!;

    public Task<AuthSessionRecord?> RotateRefreshAsync(
        string oldRefreshHash,
        string newAccessHash,
        string newRefreshHash,
        DateTime accessExpiresAt,
        DateTime refreshExpiresAt,
        CancellationToken ct = default)
    {
        var filter = Builders<AuthSessionRecord>.Filter.And(
            Builders<AuthSessionRecord>.Filter.Eq(x => x.RefreshTokenHash, oldRefreshHash),
            Builders<AuthSessionRecord>.Filter.Eq(x => x.RevokedAt, null));

        var update = Builders<AuthSessionRecord>.Update
            .Set(x => x.AccessTokenHash, newAccessHash)
            .Set(x => x.RefreshTokenHash, newRefreshHash)
            .Set(x => x.AccessExpiresAt, accessExpiresAt)
            .Set(x => x.RefreshExpiresAt, refreshExpiresAt);

        return _collection.FindOneAndUpdateAsync(
            filter, update,
            new FindOneAndUpdateOptions<AuthSessionRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }

    public async Task RevokeByRefreshHashAsync(string hash, DateTime now, CancellationToken ct = default)
    {
        await _collection.UpdateOneAsync(
            x => x.RefreshTokenHash == hash && x.RevokedAt == null,
            Builders<AuthSessionRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct);
    }

    public async Task RevokeByAccessHashAsync(string hash, DateTime now, CancellationToken ct = default)
    {
        await _collection.UpdateOneAsync(
            x => x.AccessTokenHash == hash && x.RevokedAt == null,
            Builders<AuthSessionRecord>.Update.Set(x => x.RevokedAt, now),
            cancellationToken: ct);
    }
}

public sealed class AuditRepository
{
    private readonly IMongoCollection<AuthAuditLogRecord> _collection;

    public AuditRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthAuditLogRecord>(AuthCollectionNames.AuditLogs);
    }

    public Task InsertAsync(AuthAuditLogRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<List<AuthAuditLogRecord>> FindAllAsync(CancellationToken ct = default) =>
        _collection.Find(FilterDefinition<AuthAuditLogRecord>.Empty).ToListAsync(ct);
}
