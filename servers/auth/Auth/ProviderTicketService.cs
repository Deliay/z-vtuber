using MongoDB.Bson;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Auth.Auth;

public sealed record ProviderTicketResult(string Ticket, string RedirectUrl, DateTime ExpiresAt);

/// <summary>一次性 Provider Ticket：60 秒、只存 HMAC、绑定授权参数；confirm 时生成。</summary>
public sealed class ProviderTicketService(
    ProviderTicketRepository repository,
    TokenHasher hasher,
    IOptions<AuthOptions> authOptions,
    IOptions<OAuthProviderOptions> providerOptions,
    TimeProvider clock,
    ILogger<ProviderTicketService> logger)
{
    public async Task<ProviderTicketResult> CreateAsync(
        ObjectId userId,
        string returnTo,
        CancellationToken ct = default)
    {
        var provider = providerOptions.Value;
        var request = ReturnToValidator.Validate(returnTo, provider);
        var now = clock.GetUtcNow().UtcDateTime;
        var ticket = TokenGenerator.CreateOpaqueToken();
        var expiresAt = now.AddSeconds(authOptions.Value.ProviderTicketSeconds);

        await repository.InsertAsync(new AuthProviderTicketRecord
        {
            TicketHash = hasher.Hash(ticket),
            UserId = userId,
            ClientId = request.ClientId,
            RedirectUri = request.RedirectUri,
            State = request.State ?? string.Empty,
            Scope = request.Scope,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            ExpiresAt = expiresAt,
            CreatedAt = now
        }, ct);

        logger.LogInformation("Provider ticket created for user {UserId}.", userId);
        return new ProviderTicketResult(ticket, ReturnToValidator.AppendTicket(returnTo, ticket), expiresAt);
    }

    /// <summary>一次性消费 ticket；client/redirect 必须与 ticket 记录一致。</summary>
    public async Task<AuthProviderTicketRecord?> ConsumeAsync(
        string ticket, string clientId, string redirectUri, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return await repository.TryConsumeAsync(hasher.Hash(ticket), clientId, redirectUri, now, ct);
    }

    public Task RevokeAsync(string ticket, CancellationToken ct = default) =>
        repository.RevokeAsync(hasher.Hash(ticket), clock.GetUtcNow().UtcDateTime, ct);
}
