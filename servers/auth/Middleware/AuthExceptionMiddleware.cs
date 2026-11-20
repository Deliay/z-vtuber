using System.Text.Json;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Contracts;

namespace Z.Vtuber.Auth.Middleware;

/// <summary>
/// 账号中心 API 统一错误体；<c>/oauth/*</c> 使用 OAuth 标准错误体并禁用缓存。
/// </summary>
public sealed class AuthExceptionMiddleware(
    RequestDelegate next,
    ILogger<AuthExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        var isOAuth = context.Request.Path.StartsWithSegments("/oauth");
        if (isOAuth && !context.Response.HasStarted)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }

        try
        {
            await next(context);
        }
        catch (AuthException ex)
        {
            if (isOAuth)
            {
                await WriteOAuthAsync(context, ex.StatusCode, MapOAuthError(ex.StatusCode), ex.Message);
            }
            else
            {
                await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message);
            }
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error while processing {Method} {Path}.",
                context.Request.Method, context.Request.Path);
            if (isOAuth)
            {
                await WriteOAuthAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "server_error",
                    "The authorization server encountered an unexpected condition.");
            }
            else
            {
                await WriteAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    AuthErrorCodes.Internal,
                    "服务暂时不可用，请稍后重试。");
            }
        }
    }

    private static string MapOAuthError(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => "invalid_client",
        StatusCodes.Status429TooManyRequests => "too_many_requests",
        _ => "invalid_request"
    };

    private static Task WriteOAuthAsync(HttpContext context, int status, string error, string description) =>
        WriteJsonAsync(context, status, new { error, error_description = description });

    private static Task WriteAsync(HttpContext context, int status, string code, string message) =>
        WriteJsonAsync(context, status, new ErrorBody { Error = new ErrorDetail { Code = code, Message = message } });

    private static async Task WriteJsonAsync(HttpContext context, int status, object body)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
