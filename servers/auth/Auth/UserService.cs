using MongoDB.Driver;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Auth;

public sealed record AuthUserView(string Platform, long Uid, string Name, string? AvatarUrl);

public sealed record UserUpsertResult(AuthUserRecord User, bool Disabled, bool Created);

/// <summary>
/// 平台用户：以 <c>(Platform, PlatformUid)</c> 唯一 upsert；重复键竞态回读；禁用返回拒绝。
/// </summary>
public sealed class UserService(
    UserRepository repository,
    TimeProvider clock,
    ILogger<UserService> logger)
{
    public const string Bilibili = "bilibili";

    public async Task<UserUpsertResult> UpsertBilibiliUserAsync(
        long uid,
        string displayName,
        string? avatarUrl,
        string? sourceRoomId,
        string? clientIp,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var name = string.IsNullOrWhiteSpace(displayName) ? $"UID:{uid}" : displayName;

        var existing = await repository.FindByPlatformUidAsync(Bilibili, uid, ct);
        if (existing is not null)
        {
            if (string.Equals(existing.Status, AuthUserStatus.Disabled, StringComparison.Ordinal))
            {
                return new UserUpsertResult(existing, true, false);
            }

            var updated = await repository.UpdateLoginAsync(
                existing.Id, name, avatarUrl, sourceRoomId, clientIp, now, ct);
            return new UserUpsertResult(updated ?? existing, false, false);
        }

        var record = new AuthUserRecord
        {
            Platform = Bilibili,
            PlatformUid = uid,
            DisplayName = name,
            AvatarUrl = avatarUrl,
            Status = AuthUserStatus.Active,
            ProviderSubject = ProviderSubject(uid),
            CreatedAt = now,
            LastLoginAt = now,
            LastLoginIp = clientIp,
            LastSourceRoomId = sourceRoomId,
            LoginCount = 1
        };

        try
        {
            await repository.InsertAsync(record, ct);
            return new UserUpsertResult(record, false, true);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            logger.LogInformation("Concurrent user upsert detected for bilibili uid {Uid}; reading back.", uid);
            var raced = await repository.FindByPlatformUidAsync(Bilibili, uid, ct);
            if (raced is null)
            {
                throw;
            }

            if (string.Equals(raced.Status, AuthUserStatus.Disabled, StringComparison.Ordinal))
            {
                return new UserUpsertResult(raced, true, false);
            }

            var updated = await repository.UpdateLoginAsync(
                raced.Id, name, avatarUrl, sourceRoomId, clientIp, now, ct);
            return new UserUpsertResult(updated ?? raced, false, false);
        }
    }

    public static string ProviderSubject(long uid) => $"bili_{uid}";

    public static AuthUserView ToView(AuthUserRecord user) =>
        new(user.Platform, user.PlatformUid, user.DisplayName, user.AvatarUrl);
}
