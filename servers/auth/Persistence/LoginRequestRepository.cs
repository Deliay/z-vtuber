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
        // 最终 CAS 必须同时校验 2 分钟确认窗口，避免窗口过期后仍签发凭证。
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Confirming),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.ConfirmNonce, nonce),
            Builders<AuthLoginRequestRecord>.Filter.Gt(x => x.ConfirmExpiresAt, now));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Consumed)
            .Set(x => x.ConsumedAt, now)
            .Unset(x => x.ConfirmNonce)
            .Inc(x => x.Version, 1);

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <summary>不可恢复的确认失败：<c>confirming → failed</c> 并记录错误码。</summary>
    public async Task<bool> TryMarkFailedAsync(
        string requestId, string nonce, string failureCode, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Confirming),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.ConfirmNonce, nonce));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Failed)
            .Set(x => x.FailureCode, failureCode)
            .Unset(x => x.ConfirmNonce)
            .Inc(x => x.Version, 1);

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <summary>把超过确认操作超时的 <c>confirming</c> 回退为 <c>verified</c>（确认窗口仍有效时）。</summary>
    public async Task<bool> TryRevertStaleConfirmAsync(
        string requestId, DateTime startedBefore, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Confirming),
            Builders<AuthLoginRequestRecord>.Filter.Lte(x => x.ConfirmStartedAt, startedBefore));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Verified)
            .Unset(x => x.ConfirmNonce)
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
        // 状态机只允许 pending/verified → cancelled；confirming 必须走成功/回退路径。
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.RequestId, requestId),
            Builders<AuthLoginRequestRecord>.Filter.In(x => x.Status,
                new[] { LoginStatus.Pending, LoginStatus.Verified }));

        var update = Builders<AuthLoginRequestRecord>.Update
            .Set(x => x.Status, LoginStatus.Cancelled)
            .Set(x => x.CancelledAt, now)
            .Inc(x => x.Version, 1);

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task<List<AuthLoginRequestRecord>> FindExpiredAsync(
        DateTime now, TimeSpan confirmTimeout, int limit, CancellationToken ct = default)
    {
        var pending = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Pending),
            Builders<AuthLoginRequestRecord>.Filter.Lte(x => x.ExpiresAt, now));
        var verified = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Verified),
            Builders<AuthLoginRequestRecord>.Filter.Lte(x => x.ConfirmExpiresAt, now));
        var staleConfirm = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, LoginStatus.Confirming),
            Builders<AuthLoginRequestRecord>.Filter.Lte(x => x.ConfirmStartedAt, now - confirmTimeout));

        return _collection.Find(Builders<AuthLoginRequestRecord>.Filter.Or(pending, verified, staleConfirm))
            .Limit(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 超时迁移：确认窗口仍有效时 <c>confirming → verified</c>（可重试）；
    /// 否则 <c>pending/verified/confirming → expired</c>。仅当 CAS 命中返回 <c>true</c>，供扫描器去重审计。
    /// </summary>
    public async Task<bool> TryExpireAsync(AuthLoginRequestRecord record, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<AuthLoginRequestRecord>.Filter.And(
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Id, record.Id),
            Builders<AuthLoginRequestRecord>.Filter.Eq(x => x.Status, record.Status));

        var update = string.Equals(record.Status, LoginStatus.Confirming, StringComparison.Ordinal) &&
                     record.ConfirmExpiresAt > now
            ? Builders<AuthLoginRequestRecord>.Update
                .Set(x => x.Status, LoginStatus.Verified)
                .Unset(x => x.ConfirmNonce)
                .Inc(x => x.Version, 1)
            : Builders<AuthLoginRequestRecord>.Update
                .Set(x => x.Status, LoginStatus.Expired)
                .Inc(x => x.Version, 1);

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}
