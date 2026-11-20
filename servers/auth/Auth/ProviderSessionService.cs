using MongoDB.Bson;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Auth;

public sealed record ProviderSessionContext(AuthProviderSessionRecord Session, AuthUserRecord User);

/// <summary>Casdoor Provider Session：32 字节 Cookie 值、只存 HMAC、30 天滑动过期。</summary>
public sealed class ProviderSessionService(
    ProviderSessionRepository repository,
    UserRepository userRepository,
    TokenHasher hasher,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<ProviderSessionService> logger)
{
    public const string CookieName = "auth_provider_session";

    public async Task<string> CreateAsync(
        ObjectId userId,
        string? clientIp,
        string? userAgent,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = TokenGenerator.CreateOpaqueToken();
        await repository.InsertAsync(new AuthProviderSessionRecord
        {
            SessionIdHash = hasher.Hash(token),
            UserId = userId,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(options.Value.ProviderSessionDays),
            ClientIp = clientIp,
            UserAgent = Truncate(userAgent)
        }, ct);

        logger.LogInformation("Provider session created for user {UserId}.", userId);
        return token;
    }

    public async Task<ProviderSessionContext?> ResolveAsync(
        string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var record = await repository.FindByHashAsync(hasher.Hash(token), ct);
        if (record is null || record.RevokedAt is not null)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (record.ExpiresAt <= now)
        {
            return null;
        }

        var user = await userRepository.FindByIdAsync(record.UserId, ct);
        if (user is null || !string.Equals(user.Status, AuthUserStatus.Active, StringComparison.Ordinal))
        {
            return null;
        }

        if (now - record.LastSeenAt >= TimeSpan.FromMinutes(5))
        {
            await repository.TouchAsync(record.Id, now, now.AddDays(options.Value.ProviderSessionDays), ct);
        }

        return new ProviderSessionContext(record, user);
    }

    public Task RevokeAsync(string? token, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.CompletedTask
            : repository.RevokeByHashAsync(hasher.Hash(token), clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>注销统一登录时撤销该用户的全部 Provider Session，避免 Casdoor SSO 静默重发 code。</summary>
    public Task RevokeAllForUserAsync(ObjectId userId, CancellationToken ct = default) =>
        repository.RevokeAllByUserAsync(userId, clock.GetUtcNow().UtcDateTime, ct);

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 256 ? value : value[..256];
}

/// <summary>Provider Session Cookie 的安全属性。</summary>
public static class ProviderSessionCookie
{
    public static bool IsSecure(string publicBaseUrl) =>
        Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public static CookieOptions BuildOptions(string publicBaseUrl, DateTime expiresAt) => new()
    {
        HttpOnly = true,
        Secure = IsSecure(publicBaseUrl),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true
    };

    public static void Append(HttpResponse response, string publicBaseUrl, string token, DateTime expiresAt) =>
        response.Cookies.Append(ProviderSessionService.CookieName, token, BuildOptions(publicBaseUrl, expiresAt));

    public static void Delete(HttpResponse response, string publicBaseUrl) =>
        response.Cookies.Delete(ProviderSessionService.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = IsSecure(publicBaseUrl),
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
}
