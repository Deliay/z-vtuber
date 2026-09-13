using System.Text.Json;

namespace Z.Vtuber.Cookie.Platform;

/// <summary>Bilibili 平台差异：登录页、扫码二维码、nav 登录态检测与 mid/uname 身份解析。</summary>
public sealed class BilibiliCookiePlatformProfile : ICookiePlatformProfile
{
    public string Platform => "bilibili";
    public string LoginUrl => "https://passport.bilibili.com/login";
    public string QrCodeSelector => "[class*='login-scan__qrcode']";
    public string HomeUrl => "https://www.bilibili.com";
    public string CookieDomainUrl => "https://www.bilibili.com";
    public string? LoginStateObservedUrlFragment => "/x/web-interface/nav";
    public string? LoginStateProbeUrl => null;
    public string LockKeyPrefix => string.Empty;

    public bool IsLoggedIn(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("isLogin", out var isLogin)
                && isLogin.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public IdentityExtractionResult ExtractIdentity(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new IdentityExtractionResult();
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return new IdentityExtractionResult();
            }

            long? mid = data.TryGetProperty("mid", out var midElement) && midElement.ValueKind == JsonValueKind.Number
                ? midElement.GetInt64()
                : null;
            var uname = data.TryGetProperty("uname", out var unameElement) && unameElement.ValueKind == JsonValueKind.String
                ? unameElement.GetString()
                : null;

            return new IdentityExtractionResult { Mid = mid, Uname = uname };
        }
        catch (JsonException)
        {
            return new IdentityExtractionResult();
        }
    }
}
