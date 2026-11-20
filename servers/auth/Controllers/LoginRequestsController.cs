using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Contracts;
using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Auth.Controllers;

[ApiController]
[Route("api/auth/login-requests")]
public sealed class LoginRequestsController(
    LoginRequestService service,
    IAuthRateLimiter rateLimiter,
    DanmakuHealthRegistry registry,
    IOptions<DanmakuOptions> danmakuOptions,
    IOptions<AuthOptions> authOptions,
    ProviderSessionService providerSessions) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateLoginRequestRequest? body, CancellationToken ct)
    {
        var ip = ClientIp();
        if (!await rateLimiter.TryTokenPerMinuteAsync(ip, ct) ||
            !await rateLimiter.TryTokenPerDayAsync(ip, ct) ||
            (!string.IsNullOrWhiteSpace(body?.ClientId) &&
             !await rateLimiter.TryTokenPerClientAsync(body!.ClientId!, ct)))
        {
            throw AuthException.RateLimited();
        }

        var rooms = danmakuOptions.Value.RoomIds;
        var snapshots = registry.GetSnapshots(rooms);
        if (!snapshots.Any(x => x.Available))
        {
            throw AuthException.SourceUnavailable();
        }

        var created = await service.CreateAsync(
            body?.ClientId,
            body?.ClientNonce,
            body?.ReturnTo,
            ip,
            Request.Headers.UserAgent.ToString(),
            ct);

        var response = new CreatedLoginRequestResponse
        {
            RequestId = created.RequestId,
            Token = created.Token,
            PollToken = created.PollToken,
            ExpiresAt = created.ExpiresAt,
            PollAfterMs = created.PollAfterMs,
            RoomIds = created.RoomIds,
            PrimaryRoomId = created.PrimaryRoomId,
            ConfirmWindowSeconds = created.ConfirmWindowSeconds,
            Instruction = created.Instruction
        };
        return Created($"/api/auth/login-requests/{created.RequestId}", response);
    }

    [HttpGet("{requestId}")]
    public async Task<IActionResult> Get(string requestId, CancellationToken ct)
    {
        var (_, view) = await service.GetAsync(requestId, PollToken(), ct);
        return Ok(new LoginRequestStatusResponse
        {
            Status = view.Status,
            ExpiresAt = view.ExpiresAt,
            ConfirmExpiresAt = view.ConfirmExpiresAt,
            SourceRoomId = view.SourceRoomId,
            User = ToDto(view.User),
            RetryAfterMs = view.RetryAfterMs
        });
    }

    [HttpPost("{requestId}/confirm")]
    public async Task<IActionResult> Confirm(string requestId, CancellationToken ct)
    {
        var result = await service.ConfirmAsync(requestId, PollToken(), ClientIp(), Request.Headers.UserAgent.ToString(), ct);
        if (string.Equals(result.Mode, AuthModes.Standalone, StringComparison.Ordinal))
        {
            var token = await providerSessions.CreateAsync(
                result.UserId, ClientIp(), Request.Headers.UserAgent.ToString(), ct);
            ProviderSessionCookie.Append(
                Response,
                authOptions.Value.PublicBaseUrl,
                token,
                DateTime.UtcNow.AddDays(authOptions.Value.ProviderSessionDays));
        }

        return Ok(new ConfirmLoginResponse
        {
            Mode = result.Mode,
            User = new AuthUserDto
            {
                Platform = result.User.Platform,
                Uid = result.User.Uid,
                Name = result.User.Name,
                AvatarUrl = result.User.AvatarUrl
            },
            RedirectUrl = result.RedirectUrl,
            ProviderTicketExpiresAt = result.ProviderTicketExpiresAt,
            Tokens = result.Tokens is null
                ? null
                : new StandaloneTokensDto
                {
                    TokenType = result.Tokens.TokenType,
                    AccessToken = result.Tokens.AccessToken,
                    ExpiresIn = result.Tokens.ExpiresIn,
                    RefreshToken = result.Tokens.RefreshToken,
                    RefreshExpiresIn = result.Tokens.RefreshExpiresIn
                }
        });
    }

    [HttpPost("{requestId}/cancel")]
    public async Task<IActionResult> Cancel(string requestId, CancellationToken ct)
    {
        await service.CancelAsync(requestId, PollToken(), ClientIp(), ct);
        return NoContent();
    }

    private string? PollToken() => Request.Headers["X-Poll-Token"].ToString();

    private string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static AuthUserDto? ToDto(AuthUserView? user) => user is null
        ? null
        : new AuthUserDto
        {
            Platform = user.Platform,
            Uid = user.Uid,
            Name = user.Name,
            AvatarUrl = user.AvatarUrl
        };
}
