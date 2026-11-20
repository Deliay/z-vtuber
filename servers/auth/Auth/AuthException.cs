namespace Z.Vtuber.Auth.Auth;

/// <summary>账号中心 API 的统一错误码。</summary>
public static class AuthErrorCodes
{
    public const string InvalidRequest = "AUTH_INVALID_REQUEST";
    public const string PollTokenInvalid = "AUTH_POLL_TOKEN_INVALID";
    public const string UserDisabled = "AUTH_USER_DISABLED";
    public const string LoginRequestNotFound = "AUTH_LOGIN_REQUEST_NOT_FOUND";
    public const string LoginRequestNotVerified = "AUTH_LOGIN_REQUEST_NOT_VERIFIED";
    public const string LoginRequestAlreadyConsumed = "AUTH_LOGIN_REQUEST_ALREADY_CONSUMED";
    public const string ConfirmRace = "AUTH_CONFIRM_RACE";
    public const string LoginRequestExpired = "AUTH_LOGIN_REQUEST_EXPIRED";
    public const string TokenRateLimited = "AUTH_TOKEN_RATE_LIMITED";
    public const string DanmakuRateLimited = "AUTH_DANMAKU_RATE_LIMITED";
    public const string DanmakuSourceUnavailable = "AUTH_DANMAKU_SOURCE_UNAVAILABLE";
    public const string CasdoorNotConfigured = "AUTH_CASDOOR_NOT_CONFIGURED";
    public const string Unauthorized = "AUTH_UNAUTHORIZED";
    public const string Internal = "AUTH_INTERNAL";
}

/// <summary>账号中心 API 的可预期业务异常；由中间件统一转换为错误体。</summary>
public sealed class AuthException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static AuthException Invalid(string message) =>
        new(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidRequest, message);

    public static AuthException NotFound() =>
        new(StatusCodes.Status404NotFound, AuthErrorCodes.LoginRequestNotFound, "登录请求不存在，请重新获取 token。");

    public static AuthException PollTokenInvalid() =>
        new(StatusCodes.Status403Forbidden, AuthErrorCodes.PollTokenInvalid, "轮询凭证无效。");

    public static AuthException Expired() =>
        new(StatusCodes.Status410Gone, AuthErrorCodes.LoginRequestExpired, "登录请求已过期，请重新获取 token。");

    public static AuthException NotVerified() =>
        new(StatusCodes.Status409Conflict, AuthErrorCodes.LoginRequestNotVerified, "尚未识别到弹幕，请确认已发送 token。");

    public static AuthException AlreadyConsumed() =>
        new(StatusCodes.Status409Conflict, AuthErrorCodes.LoginRequestAlreadyConsumed, "登录请求已使用，请重新获取 token。");

    public static AuthException ConfirmRace() =>
        new(StatusCodes.Status409Conflict, AuthErrorCodes.ConfirmRace, "确认请求并发冲突，请刷新后重试。");

    public static AuthException UserDisabled() =>
        new(StatusCodes.Status403Forbidden, AuthErrorCodes.UserDisabled, "账号已被禁用。");

    public static AuthException Unauthorized() =>
        new(StatusCodes.Status401Unauthorized, AuthErrorCodes.Unauthorized, "未登录或会话已过期。");

    public static AuthException RateLimited(string code = AuthErrorCodes.TokenRateLimited) =>
        new(StatusCodes.Status429TooManyRequests, code, "请求过于频繁，请稍后重试。");

    public static AuthException SourceUnavailable() =>
        new(StatusCodes.Status503ServiceUnavailable, AuthErrorCodes.DanmakuSourceUnavailable, "弹幕事件源暂不可用，请稍后重试。");
}
