using Microsoft.AspNetCore.Mvc;

namespace Z.Vtuber.Auth.Controllers;

/// <summary>auth 内嵌静态页面（无 Node/Razor）：验证/注册页与账号中心。</summary>
[ApiController]
public sealed class PagesController(
    IWebHostEnvironment environment,
    ILogger<PagesController> logger) : ControllerBase
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https://*.hdslb.com data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    [HttpGet("/connect/bilibili")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> Connect() => ServeAsync("connect/index.html");

    [HttpGet("/account")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> Account() => ServeAsync("account/index.html");

    private async Task<IActionResult> ServeAsync(string relativePath)
    {
        ApplySecurityHeaders();
        var path = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), relativePath);
        if (!System.IO.File.Exists(path))
        {
            logger.LogError("Static page {Path} is missing.", path);
            return NotFound();
        }

        var html = await System.IO.File.ReadAllTextAsync(path, HttpContext.RequestAborted);
        return Content(html, "text/html; charset=utf-8");
    }

    private void ApplySecurityHeaders()
    {
        Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Frame-Options"] = "DENY";
    }
}
