using MongoDB.Bson;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.OAuth;

/// <summary>授权码：32 字节、只存 HMAC、5 分钟、一次性、绑定 client/redirect/PKCE。</summary>
public sealed class AuthorizationCodeService(
    OAuthCodeRepository repository,
    TokenHasher hasher,
    IOptions<OAuthProviderOptions> options,
    TimeProvider clock,
    ILogger<AuthorizationCodeService> logger)
{
    public async Task<string> IssueAsync(
        ObjectId userId,
        AuthorizeRequest request,
        ObjectId? providerSessionId,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var code = TokenGenerator.CreateOpaqueToken();
        await repository.InsertAsync(new AuthOAuthCodeRecord
        {
            CodeHash = hasher.Hash(code),
            ClientId = request.ClientId,
            RedirectUri = request.RedirectUri,
            UserId = userId,
            ProviderSessionId = providerSessionId,
            Scope = request.Scope,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            ExpiresAt = now.AddSeconds(options.Value.AuthorizationCodeSeconds),
            CreatedAt = now
        }, ct);

        logger.LogInformation("Authorization code issued for user {UserId}.", userId);
        return code;
    }

    public async Task<AuthOAuthCodeRecord> ConsumeAsync(
        string code,
        string clientId,
        string redirectUri,
        string? codeVerifier,
        CancellationToken ct = default)
    {
        var record = await repository.FindByHashAsync(hasher.Hash(code), ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (record is null ||
            record.ConsumedAt is not null ||
            record.ExpiresAt <= now ||
            !TokenHasher.FixedTimeEquals(record.ClientId, clientId) ||
            !string.Equals(record.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            throw OAuthException.InvalidGrant();
        }

        if (!Pkce.Validate(
                codeVerifier,
                record.CodeChallenge,
                record.CodeChallengeMethod,
                options.Value.RequirePkce))
        {
            throw OAuthException.InvalidGrant("PKCE verification failed.");
        }

        var consumed = await repository.TryConsumeAsync(record.CodeHash, now, ct);
        if (consumed is null)
        {
            throw OAuthException.InvalidGrant("The authorization code was already used.");
        }

        return consumed;
    }
}

public sealed record ProviderTokenPair(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    int RefreshExpiresIn,
    string Scope);

/// <summary>Casdoor Provider Token：opaque、只存 HMAC、access 1h / refresh 30d、单文档原子轮换。</summary>
public sealed class ProviderTokenService(
    ProviderTokenRepository repository,
    TokenHasher hasher,
    IOptions<OAuthProviderOptions> options,
    TimeProvider clock,
    ILogger<ProviderTokenService> logger)
{
    public async Task<ProviderTokenPair> IssueAsync(
        ObjectId userId,
        string clientId,
        string scope,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var access = TokenGenerator.CreateOpaqueToken();
        var refresh = TokenGenerator.CreateOpaqueToken();
        var accessExpiresAt = now.AddSeconds(options.Value.AccessTokenSeconds);
        var refreshExpiresAt = now.AddDays(options.Value.RefreshTokenDays);

        await repository.InsertAsync(new AuthProviderTokenRecord
        {
            AccessTokenHash = hasher.Hash(access),
            RefreshTokenHash = hasher.Hash(refresh),
            UserId = userId,
            ClientId = clientId,
            Scope = scope,
            ExpiresAt = accessExpiresAt,
            RefreshExpiresAt = refreshExpiresAt,
            CreatedAt = now
        }, ct);

        logger.LogInformation("Provider tokens issued for user {UserId}.", userId);
        return new ProviderTokenPair(
            access,
            refresh,
            options.Value.AccessTokenSeconds,
            checked(options.Value.RefreshTokenDays * 24 * 3600),
            scope);
    }

    public async Task<AuthProviderTokenRecord?> ValidateAccessAsync(
        string accessToken, CancellationToken ct = default)
    {
        var record = await repository.FindByAccessHashAsync(hasher.Hash(accessToken), ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now)
        {
            return null;
        }

        return record;
    }

    public async Task<ProviderTokenPair> RotateAsync(
        string refreshToken,
        string clientId,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var record = await repository.FindByRefreshHashAsync(hasher.Hash(refreshToken), ct);
        if (record is null ||
            record.RevokedAt is not null ||
            record.RefreshExpiresAt is null ||
            record.RefreshExpiresAt <= now ||
            !TokenHasher.FixedTimeEquals(record.ClientId, clientId))
        {
            throw OAuthException.InvalidGrant();
        }

        var access = TokenGenerator.CreateOpaqueToken();
        var newRefresh = TokenGenerator.CreateOpaqueToken();
        var accessExpiresAt = now.AddSeconds(options.Value.AccessTokenSeconds);
        var refreshExpiresAt = now.AddDays(options.Value.RefreshTokenDays);

        var rotated = await repository.RotateRefreshAsync(
            hasher.Hash(refreshToken),
            hasher.Hash(access),
            hasher.Hash(newRefresh),
            accessExpiresAt,
            refreshExpiresAt,
            now,
            ct);
        if (rotated is null)
        {
            throw OAuthException.InvalidGrant("The refresh token was already used.");
        }

        return new ProviderTokenPair(
            access,
            newRefresh,
            options.Value.AccessTokenSeconds,
            checked(options.Value.RefreshTokenDays * 24 * 3600),
            rotated.Scope);
    }

    public async Task RevokeAsync(string? accessToken, string? refreshToken, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            await repository.RevokeByAccessHashAsync(hasher.Hash(accessToken), now, ct);
        }

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await repository.RevokeByRefreshHashAsync(hasher.Hash(refreshToken), now, ct);
        }
    }
}
