using MongoDB.Driver;

namespace Z.Vtuber.Auth.Persistence;

public sealed class LoginRequestRepository
{
    private readonly IMongoCollection<AuthLoginRequestRecord> _collection;

    public LoginRequestRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AuthLoginRequestRecord>(AuthCollectionNames.LoginRequests);
    }

    public Task InsertAsync(AuthLoginRequestRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public Task<AuthLoginRequestRecord?> FindByRequestIdAsync(string requestId, CancellationToken ct = default) =>
        _collection.Find(x => x.RequestId == requestId).FirstOrDefaultAsync(ct)!;

    public Task<AuthLoginRequestRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        _collection.Find(x => x.TokenHash == tokenHash).FirstOrDefaultAsync(ct)!;

    /// <summary>弹幕匹配：<c>pending &amp;&amp; 未过期</c> CAS 到 <c>verified</c>，并发只有一条成功。</summary>
    public Task<AuthLoginRequestRecord?> TryMarkVerifiedAsync(
        string tokenHash,
        string roomId,
        Danmaku.DanmakuSender sender,
        DateTime now,
        int confirmWindowSeconds,
        CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.TokenHash, tokenHash),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Pending),
            Builders<AuthLoginRequestRecord>.Filter.Gt(x => x.ExpiresAt, now));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Verified)
            .Set(x => x.VerifiedAt, now)
            .Set(x => x.ConfirmExpiresAt, now.AddSeconds(confirmWindowSeconds))
            .Set(x => x.SourceRoomId, roomId)
            .Set(x => x.SenderUid, sender.Uid)
            .Set(x => x.SenderName, sender.Name)
            .Set(x => x.SenderAvatar, sender.AvatarUrl)
            .Set(x => x.SenderMedal, sender.Medal)
            .Inc(x => x.Version, 1);

        return _collection.FindOneAndUpdateAsync(
            filter, update,
            new FindOneAndUpdateOptions<AuthLoginRequestRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }

    public Task<AuthLoginRequestRecord?> TryStartConfirmAsync(
        string requestId, string nonce, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Verified),
            Builders<AuthLoginRequestRecord>.Filter.Gt(x => x.ConfirmExpiresAt, now));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Confirming)
            .Set(x => x.ConfirmStartedAt, now)
            .Set(x => x.ConfirmNonce, nonce)
            .Inc(x => x.Version, 1);

        return _collection.FindOneAndUpdateAsync(
            filter, update,
            new FindOneAndUpdateOptions<AuthLoginRequestRecord> { ReturnDocument = ReturnDocument.After },
            ct)!;
    }

    public async Task<bool> TryMarkConsumedAsync(
        string requestId, string nonce, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Confirming),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.ConfirmNonce, nonce));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Consumed)
            .Set(x => x.ConsumedAt, now)
            .Inc(x => x.Version, 1);

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task RevertToVerifiedAsync(string requestId, string nonce, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Confirming),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.ConfirmNonce, nonce));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Verified)
            .Unset(x => x.ConfirmNonce)
            .Inc(x => x.Version, 1);

        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task<bool> TryCancelAsync(string requestId, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.In(x => x.Status,
                new[] { LoginStatus.Pending, LoginStatus.Verified, LoginStatus.Confirming }));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Cancelled)
            .Set(x => x.CancelledAt, now)
            .Inc(x => x.Version, 1);

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task<List<AuthLoginRequestRecord>> FindExpiredAsync(DateTime now, int limit, CancellationToken ct = default)
    {
        var pending = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Pending),
            Builders<AuthLoginRequestRecord>.Filter.Lte(x => x.ExpiresAt, now));
        var verified = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Verified),
            Builders<AuthLoginRequestRecord>.Filter.Lte(x => x.ConfirmExpiresAt, now));

        return _collection.Find(Builders<AuthLoginRequestRecord>.Filter.Or(pending, verified))
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task TryExpireAsync(AuthLoginRequestRecord record, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Id, record.Id),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, record.Status));
        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Expired)
            .Inc(x => x.Version, 1);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
