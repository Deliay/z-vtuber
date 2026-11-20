namespace Z.Vtuber.Auth.Contracts;

public sealed class CreateLoginRequestRequest
{
    public string? ClientId { get; set; }

    public string? ClientNonce { get; set; }

    public string? ReturnTo { get; set; }
}

public sealed class CreatedLoginRequestResponse
{
    public required string RequestId { get; init; }

    public required string Token { get; init; }

    public required string PollToken { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public required int PollAfterMs { get; init; }

    public required string[] RoomIds { get; init; }

    public required string PrimaryRoomId { get; init; }

    public required int ConfirmWindowSeconds { get; init; }

    public required string Instruction { get; init; }
}

public sealed class AuthUserDto
{
    public required string Platform { get; init; }

    public required long Uid { get; init; }

    public required string Name { get; init; }

    public string? AvatarUrl { get; init; }
}

public sealed class LoginRequestStatusResponse
{
    public required string Status { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public DateTime? ConfirmExpiresAt { get; init; }

    public string? SourceRoomId { get; init; }

    public AuthUserDto? User { get; init; }

    public required int RetryAfterMs { get; init; }
}

public sealed class StandaloneTokensDto
{
    public required string TokenType { get; init; }

    public required string AccessToken { get; init; }

    public required int ExpiresIn { get; init; }

    public required string RefreshToken { get; init; }

    public required int RefreshExpiresIn { get; init; }
}

public sealed class ConfirmLoginResponse
{
    public required string Mode { get; init; }

    public required AuthUserDto User { get; init; }

    public string? RedirectUrl { get; init; }

    public DateTime? ProviderTicketExpiresAt { get; init; }

    public StandaloneTokensDto? Tokens { get; init; }
}

public sealed class RoomDto
{
    public required string RoomId { get; init; }

    public required string Role { get; init; }

    public required bool Connected { get; init; }

    public required bool Available { get; init; }

    public DateTime? LastHeartbeatAt { get; init; }

    public DateTime? LastEventAt { get; init; }
}

public sealed class RoomListResponse
{
    public required IReadOnlyList<RoomDto> Rooms { get; init; }
}

public sealed class MeResponse
{
    public required bool Authenticated { get; init; }

    public AuthUserDto? User { get; init; }

    public string? ProviderSubject { get; init; }

    public string? BilibiliUid { get; init; }

    public string? BilibiliRoomId { get; init; }

    public required string Mode { get; init; }

    public string? ReauthorizeUrl { get; init; }
}

public sealed class LogoutResponse
{
    /// <summary>Casdoor end-session 地址；为空表示仅退出本服务会话。</summary>
    public string? LogoutUrl { get; init; }
}

public sealed class RefreshSessionRequest
{
    public string? RefreshToken { get; set; }
}

public sealed class LogoutSessionRequest
{
    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }
}

public sealed class ErrorBody
{
    public required ErrorDetail Error { get; init; }
}

public sealed class ErrorDetail
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}
