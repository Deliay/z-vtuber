using MongoDB.Bson;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Auth;

public sealed record StandaloneTokens(
    string TokenType,
    string AccessToken,
    int ExpiresIn,
    string RefreshToken,
    int RefreshExpiresIn);

public sealed record StandaloneSessionContext(AuthSessionRecord Session, AuthUserRecord User);

/// <summary>standalone 模式的 opaque access/refresh 会话（900s / 30d、轮换、撤销）。</summary>
public sealed class StandaloneSessionService(
    StandaloneSessionRepository repository,
    UserRepository userRepository,
    TokenHasher hasher,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<StandaloneSessionService> logger)
{
    public async Task<StandaloneTokens> IssueAsync(
        ObjectId userId,
        string? clientIp,
        string? userAgent,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var access = TokenGenerator.CreateOpaqueToken();
        var refresh = TokenGenerator.CreateOpaqueToken();
        var accessExpiresAt = now.AddSeconds(options.Value.Standalone.AccessTokenSeconds);
        var refreshExpiresAt = now.AddDays(options.Value.Standalone.RefreshTokenDays);

        await repository.InsertAsync(new AuthSessionRecord
        {
            FamilyId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            AccessTokenHash = hasher.Hash(access),
            RefreshTokenHash = hasher.Hash(refresh),
            AccessExpiresAt = accessExpiresAt,
            RefreshExpiresAt = refreshExpiresAt,
            CreatedAt = now,
            ClientIp = clientIp,
            UserAgent = Truncate(userAgent)
        }, ct);

        logger.LogInformation("Standalone session issued for user {UserId}.", userId);
        return new StandaloneTokens(
            "Bearer",
            access,
            options.Value.Standalone.AccessTokenSeconds,
            refresh,
            checked(options.Value.Standalone.RefreshTokenDays * 24 * 3600));
    }

    public async Task<StandaloneSessionContext?> ResolveAsync(
        string? accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var record = await repository.FindByAccessHashAsync(hasher.Hash(accessToken), ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (record is null || record.RevokedAt is not null || record.AccessExpiresAt <= now)
        {
            return null;
        }

        var user = await userRepository.FindByIdAsync(record.UserId, ct);
        if (user is null || !string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal))
        {
            return null;
        }

        return new StandaloneSessionContext(record, user);
    }

    public async Task<StandaloneTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var record = await repository.FindByRefreshHashAsync(hasher.Hash(refreshToken), ct);
        if (record is null || record.RevokedAt is not null || record.RefreshExpiresAt <= now)
        {
            throw AuthException.Unauthorized();
        }

        var access = TokenGenerator.CreateOpaqueToken();
        var refresh = TokenGenerator.CreateOpaqueToken();
        var accessExpiresAt = now.AddSeconds(options.Value.Standalone.AccessTokenSeconds);
        var refreshExpiresAt = now.AddDays(options.Value.Standalone.RefreshTokenDays);

        var rotated = await repository.RotateRefreshAsync(
            hasher.Hash(refreshToken),
            hasher.Hash(access),
            hasher.Hash(refresh),
            accessExpiresAt,
            refreshExpiresAt,
            ct);
        if (rotated is null)
        {
            throw AuthException.Unauthorized();
        }

        return new StandaloneTokens(
            "Bearer",
            access,
            options.Value.Standalone.AccessTokenSeconds,
            refresh,
            checked(options.Value.Standalone.RefreshTokenDays * 24 * 3600));
    }

    public Task LogoutAsync(string? accessToken, string? refreshToken, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return Task.WhenAll(
            string.IsNullOrWhiteSpace(accessToken)
                ? Task.CompletedTask
                : repository.RevokeByAccessHashAsync(hasher.Hash(accessToken), now, ct),
            string.IsNullOrWhiteSpace(refreshToken)
                ? Task.CompletedTask
                : repository.RevokeByRefreshHashAsync(hasher.Hash(refreshToken), now, ct));
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 256 ? value : value[..256];
}
