using Z.Vtuber.Cookie.Browser;

namespace Z.Vtuber.Cookie.Platform;

public sealed class BilibiliCookiePlatformProfile : ICookiePlatformProfile
{
    public string Platform => "bilibili";
    public string LoginUrl => "https://passport.bilibili.com/login";
    public string QrCodeSelector => "[class*='login-scan__qrcode']";
    public string HomeUrl => "https://www.bilibili.com";
    public string CookieDomainUrl => "https://www.bilibili.com";

    public async Task<bool> IsLoggedInAsync(IBrowserSessionManager sessionManager, string accountId, CancellationToken ct = default)
    {
        // 现有逻辑：等待 nav 响应并检查 isLogin
        var navResponse = await sessionManager.WaitForNavResponseAsync(accountId, TimeSpan.FromSeconds(10), ct);
        if (navResponse == null) return false;
        
        using var doc = System.Text.Json.JsonDocument.Parse(navResponse);
        return doc.RootElement.TryGetProperty("isLogin", out var isLogin) && isLogin.GetBoolean();
    }

    public async Task<IdentityExtractionResult> ExtractIdentityAsync(IBrowserSessionManager sessionManager, string accountId, CancellationToken ct = default)
    {
        var navResponse = await sessionManager.WaitForNavResponseAsync(accountId, TimeSpan.FromSeconds(10), ct);
        if (navResponse == null) return new IdentityExtractionResult();
        
        using var doc = System.Text.Json.JsonDocument.Parse(navResponse);
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            var mid = data.TryGetProperty("mid", out var midElement) ? midElement.GetInt64() : null;
            var uname = data.TryGetProperty("uname", out var unameElement) ? unameElement.GetString() : null;
            return new IdentityExtractionResult { Mid = mid, Uname = uname };
        }
        return new IdentityExtractionResult();
    }
}