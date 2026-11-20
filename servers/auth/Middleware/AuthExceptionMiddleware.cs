using System.Text.Json;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Contracts;

namespace Z.Vtuber.Auth.Middleware;

/// <summary>账号中心 API 统一错误体；OAuth 控制器自行输出标准错误体。</summary>
public sealed class AuthExceptionMiddleware(
    RequestDelegate next,
    ILogger<AuthExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AuthException ex)
        {
            await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error while processing {Method} {Path}.",
                context.Request.Method, context.Request.Path);
            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                AuthErrorCodes.Internal,
                "服务暂时不可用，请稍后重试。");
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new ErrorBody { Error = new ErrorDetail { Code = code, Message = message } };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
